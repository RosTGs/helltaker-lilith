"""Grid model derived from live observations; validate each route against Unity."""
from collections import deque
from dataclasses import dataclass, replace
import heapq
import itertools
import time

ACTIONS = ("up", "down", "left", "right")
DIRECTIONS = ((0, 1), (0, -1), (-1, 0), (1, 0))


@dataclass(frozen=True)
class State:
    player: tuple
    blocks: tuple
    will: int
    parity: int = 0
    key: bool = False
    keys: tuple = ()
    doors: tuple = ()


class Puzzle:
    """Turn puzzles: movement, kicks, crates, skeletons, keys and alternating spikes."""
    def __init__(self, observation):
        self.observation = observation
        self.scene = observation["scene"]["index"]
        entities = observation["entities"]
        self.void = set()
        self.walls = set()
        self.spikes = {}
        blocks, keys, doors = [], [], []
        positions = []
        for e in entities:
            cell = tuple(e["cell"])
            kind = e["kind"]
            if kind in ("void", "wall", "breakable", "unBreakable", "spikes"):
                positions.append(cell)
            if kind == "void": self.void.add(cell)
            elif kind == "wall" and e.get("blocking", False):
                if "lock" in e["name"].lower(): doors.append(cell)
                else: self.walls.add(cell)
            elif kind in ("breakable", "unBreakable") and e.get("blocking", False):
                blocks.append((cell[0], cell[1], kind))
            elif kind == "spikes": self.spikes[cell] = (e["up"], e["static"])
            elif kind == "key": keys.append(cell)
        p = observation["player"]
        positions.append(tuple(p["cell"]))
        self.bounds = (min(x for x, y in positions), max(x for x, y in positions), min(y for x, y in positions), max(y for x, y in positions))
        self.goals = {tuple(e["cell"]) for e in entities if e["kind"] == "goal" and self.inside(tuple(e["cell"])) and "hint" not in e["name"].lower()}
        self.initial = State(tuple(p["cell"]), tuple(sorted(blocks)), p["will"], key=p["key"], keys=tuple(sorted(keys)), doors=tuple(sorted(doors)))
        self.distance = self._distances()

    def inside(self, p):
        xmin, xmax, ymin, ymax = self.bounds
        return xmin <= p[0] <= xmax and ymin <= p[1] <= ymax

    def _distances(self):
        distances = {p: 0 for p in self.goals}
        queue = deque(self.goals)
        while queue:
            p = queue.popleft()
            for dx, dy in DIRECTIONS:
                q = p[0] + dx, p[1] + dy
                if self.inside(q) and q not in distances and q not in self.void and q not in self.walls:
                    distances[q] = distances[p] + 1
                    queue.append(q)
        return distances

    def solved(self, state):
        return state.player in self.goals

    def signature(self, state):
        return state.player, state.blocks, state.parity, state.key, state.keys, state.doors

    def active_spike(self, cell, parity):
        up, static = self.spikes.get(cell, (False, True))
        return up if static else up ^ bool(parity)

    def step(self, state, action):
        if isinstance(action, str): action = ACTIONS.index(action)
        if state.will <= 0 or self.solved(state): return state
        dx, dy = DIRECTIONS[action]
        dest = (state.player[0] + dx, state.player[1] + dy)
        if dest in self.void or not self.inside(dest): return state
        blocks = {(x, y): kind for x, y, kind in state.blocks}
        doors = set(state.doors)
        position = state.player
        blocked_door = dest in doors and not state.key
        if dest in self.walls or blocked_door:
            pass
        elif dest in blocks:
            target = (dest[0] + dx, dest[1] + dy)
            blocked = target in self.void or target in self.walls or target in blocks or target in doors or not self.inside(target)
            if blocked:
                if blocks[dest] == "breakable": del blocks[dest]
            else:
                blocks[target] = blocks.pop(dest)
        else:
            position = dest
            doors.discard(dest)
        parity = (state.parity + 1) % 2
        will = state.will if state.will >= 50 else state.will - 1
        if self.active_spike(position, parity) and will > 0 and will < 50: will -= 1
        for cell in list(blocks):
            if blocks[cell] == "breakable" and self.active_spike(cell, parity): del blocks[cell]
        key = state.key or position in state.keys
        keys = tuple(p for p in state.keys if p != position)
        return State(position, tuple(sorted((x, y, kind) for (x, y), kind in blocks.items())), will, parity, key, keys, tuple(sorted(doors)))

    def solve(self, max_nodes=750000, timeout=30):
        counter = itertools.count()
        start = self.initial
        heap = [(self.distance.get(start.player, 999), next(counter), start, ())]
        best = {self.signature(start): start.will}
        started = time.monotonic()
        expanded = 0
        while heap:
            _, _, state, path = heapq.heappop(heap)
            if self.solved(state): return list(path), {"expanded": expanded, "seconds": time.monotonic() - started}
            if best.get(self.signature(state), -1) > state.will: continue
            expanded += 1
            if expanded > max_nodes or time.monotonic() - started > timeout:
                raise RuntimeError(f"Search budget reached: {expanded} states")
            for action in range(4):
                new = self.step(state, action)
                if new == state or (new.will <= 0 and not self.solved(new)): continue
                key = self.signature(new)
                if best.get(key, -1) >= new.will: continue
                best[key] = new.will
                score = start.will - new.will + self.distance.get(new.player, 999)
                heapq.heappush(heap, (score, next(counter), new, path + (ACTIONS[action],)))
        raise RuntimeError("The current model needs additional rules or a fresh puzzle observation")

    def ascii(self, state=None):
        state = state or self.initial
        cells = {p: "#" for p in self.void | self.walls}
        cells.update({p: "G" for p in self.goals})
        cells.update({p: "^" if self.active_spike(p, state.parity) else "v" for p in self.spikes})
        cells.update({p: "K" for p in state.keys})
        cells.update({p: "L" for p in state.doors})
        cells.update({(x, y): "S" if kind == "breakable" else "C" for x, y, kind in state.blocks})
        cells[state.player] = "@"
        xmin, xmax, ymin, ymax = self.bounds
        return "\n".join("".join(cells.get((x, y), ".") for x in range(xmin, xmax+1)) for y in range(ymax, ymin-1, -1))
