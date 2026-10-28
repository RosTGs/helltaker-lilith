"""Neural imitation baseline and DQN training from environment transitions."""
import argparse
from collections import deque
import json
from pathlib import Path
import random
import time

import numpy as np
import torch
from torch import nn

from .client import Client, DEFAULT_CONNECTION
from .puzzle import ACTIONS, Puzzle


def encode(puzzle, state):
    xmin, xmax, ymin, ymax = puzzle.bounds
    width, height = xmax - xmin + 1, ymax - ymin + 1
    grid = np.zeros((9, height, width), dtype=np.float32)

    def put(channel, cell):
        x, y = cell
        if xmin <= x <= xmax and ymin <= y <= ymax:
            grid[channel, y - ymin, x - xmin] = 1

    put(0, state.player)
    for cell in puzzle.void | puzzle.walls: put(1, cell)
    for x, y, kind in state.blocks: put(2 if kind == "unBreakable" else 3, (x, y))
    for cell in puzzle.goals: put(4, cell)
    for cell in puzzle.spikes: put(5 if puzzle.active_spike(cell, state.parity) else 6, cell)
    for cell in state.keys: put(7, cell)
    for cell in state.doors: put(8, cell)
    return np.concatenate([grid.ravel(), np.asarray([min(state.will, 50) / 50, float(state.key)], dtype=np.float32)])


class Policy(nn.Module):
    def __init__(self, size):
        super().__init__()
        self.layers = nn.Sequential(nn.Linear(size, 128), nn.ReLU(), nn.Linear(128, 128), nn.ReLU(), nn.Linear(128, 4))

    def forward(self, x):
        return self.layers(x)


def save(path, model, puzzle, method, metrics):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    torch.save(dict(weights=model.state_dict(), input_size=len(encode(puzzle, puzzle.initial)), scene=puzzle.scene,
                    bounds=puzzle.bounds, method=method, metrics=metrics), path)


def load(path):
    checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    policy = Policy(checkpoint["input_size"])
    policy.load_state_dict(checkpoint["weights"])
    policy.eval()
    return policy, checkpoint


def imitate(scene_file, trace_file, output, epochs=800):
    """Fit a compact policy to a route already executed and checked in Unity."""
    initial = json.loads(Path(scene_file).read_text(encoding="utf-8"))
    trace = json.loads(Path(trace_file).read_text(encoding="utf-8"))
    puzzle = Puzzle(initial)
    inputs, targets = [], []
    observation = initial
    for transition in trace:
        if transition.get("discrepancy"):
            raise ValueError("Use a trace with matching simulator and Unity transitions")
        current = Puzzle(observation)
        inputs.append(encode(current, current.initial))
        targets.append(ACTIONS.index(transition["action"]))
        observation = transition["response"]["observation"]
    if not observation["goal_reached"]:
        raise ValueError("Use a completed puzzle trace")
    x = torch.from_numpy(np.stack(inputs))
    y = torch.tensor(targets, dtype=torch.long)
    model = Policy(x.shape[1])
    optimizer = torch.optim.Adam(model.parameters(), lr=0.003)
    started = time.monotonic()
    for epoch in range(epochs):
        logits = model(x)
        loss = nn.functional.cross_entropy(logits, y)
        optimizer.zero_grad(); loss.backward(); optimizer.step()
        accuracy = float((logits.argmax(1) == y).float().mean())
        if epoch > 50 and accuracy == 1 and loss.item() < 0.005: break
    metrics = dict(samples=len(trace), epochs=epoch + 1, accuracy=accuracy, loss=loss.item(), seconds=time.monotonic() - started)
    save(output, model, puzzle, "imitation_from_verified_Unity_trace", metrics)
    return metrics


def dqn(scene_file, output, episodes=3000, seed=7, warmstart=None, connection=None):
    """Learn with replay, epsilon exploration and a target network; optional live Unity backend."""
    random.seed(seed); np.random.seed(seed); torch.manual_seed(seed)
    puzzle = Puzzle(json.loads(Path(scene_file).read_text(encoding="utf-8")))
    size = len(encode(puzzle, puzzle.initial))
    model = load(warmstart)[0] if warmstart else Policy(size)
    target = Policy(size); target.load_state_dict(model.state_dict())
    optimizer = torch.optim.Adam(model.parameters(), lr=0.0005)
    memory = deque(maxlen=50000)
    client = Client(connection) if connection else None
    history, recent = [], deque(maxlen=100)
    total_steps = 0
    started = time.monotonic()
    for episode in range(episodes):
        current = Puzzle(client.reset(puzzle.scene, seed + episode)) if client else puzzle
        state = current.initial
        epsilon = max(0.05, 1.0 - episode / max(1, episodes * 0.7))
        success = False
        for step in range(128):
            x = encode(current, state)
            if random.random() < epsilon: action = random.randrange(4)
            else:
                with torch.no_grad(): action = int(model(torch.from_numpy(x)).argmax())
            before_distance = current.distance.get(state.player, 30)
            if client:
                response = client.step(ACTIONS[action])
                current = Puzzle(response["observation"])
                new = current.initial
                success = bool(response["observation"]["goal_reached"])
                dead = bool(response["observation"]["dead"]) or new.will <= 0
            else:
                new = current.step(state, action)
                success = current.solved(new)
                dead = new.will <= 0
            done = success or dead or step == 127
            reward = 1.0 if success else -1.0 if dead else -0.01
            reward += 0.03 * (before_distance - current.distance.get(new.player, 30))
            nx = encode(current, new)
            memory.append((x, action, reward, nx, done))
            state = new
            total_steps += 1
            if len(memory) >= 128 and total_steps % 4 == 0:
                batch = random.sample(memory, 64)
                bx, ba, br, bn, bd = zip(*batch)
                bx = torch.from_numpy(np.stack(bx)); bn = torch.from_numpy(np.stack(bn))
                ba = torch.tensor(ba); br = torch.tensor(br, dtype=torch.float32); bd = torch.tensor(bd, dtype=torch.float32)
                q = model(bx).gather(1, ba[:, None]).squeeze(1)
                with torch.no_grad():
                    best = model(bn).argmax(1)
                    expected = br + 0.99 * (1 - bd) * target(bn).gather(1, best[:, None]).squeeze(1)
                loss = nn.functional.smooth_l1_loss(q, expected)
                optimizer.zero_grad(); loss.backward(); nn.utils.clip_grad_norm_(model.parameters(), 5); optimizer.step()
            if total_steps % 500 == 0: target.load_state_dict(model.state_dict())
            if done: break
        recent.append(int(success))
        if episode % 100 == 0 or episode == episodes - 1:
            item = dict(episode=episode + 1, epsilon=epsilon, success_last_100=sum(recent) / len(recent), steps=total_steps)
            history.append(item); print(json.dumps(item), flush=True)
            save(output, model, puzzle, "DQN_live_Unity" if client else "DQN_simulator", dict(history=history, seconds=time.monotonic()-started))
    if client: client.release()
    return history


def play(policy_file, connection=DEFAULT_CONNECTION, output=None):
    model, checkpoint = load(policy_file)
    client = Client(connection)
    observation = client.reset(checkpoint["scene"])
    trace = []
    for _ in range(128):
        puzzle = Puzzle(observation)
        if tuple(checkpoint["bounds"]) != puzzle.bounds: raise ValueError("Policy bounds must match the loaded scene")
        x = encode(puzzle, puzzle.initial)
        with torch.no_grad(): action = ACTIONS[int(model(torch.from_numpy(x)).argmax())]
        response = client.step(action)
        observation = response["observation"]
        trace.append(dict(action=action, response=response))
        if observation["goal_reached"] or observation["dead"]: break
    result = dict(scene=checkpoint["scene"], method=checkpoint["method"], steps=len(trace), goal_reached=observation["goal_reached"], dead=observation["dead"])
    if output:
        destination = Path(output); destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(dict(result=result, trace=trace), ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    imitation = sub.add_parser("imitate")
    imitation.add_argument("scene"); imitation.add_argument("trace"); imitation.add_argument("output")
    train = sub.add_parser("train")
    train.add_argument("scene"); train.add_argument("output")
    train.add_argument("--episodes", type=int, default=3000); train.add_argument("--seed", type=int, default=7)
    train.add_argument("--warmstart"); train.add_argument("--connection")
    run = sub.add_parser("play")
    run.add_argument("policy"); run.add_argument("--connection", default=str(DEFAULT_CONNECTION)); run.add_argument("--output")
    args = parser.parse_args()
    torch.set_num_threads(2); torch.manual_seed(7)
    if args.command == "imitate": result = imitate(args.scene, args.trace, args.output)
    elif args.command == "train": result = dqn(args.scene, args.output, args.episodes, args.seed, args.warmstart, args.connection)
    else: result = play(args.policy, args.connection, args.output)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__": main()
