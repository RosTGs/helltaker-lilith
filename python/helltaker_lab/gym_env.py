"""Optional Gymnasium interface for live training and custom algorithms."""
import gymnasium as gym
from gymnasium import spaces
import numpy as np
from .client import Client, DEFAULT_CONNECTION
from .puzzle import Puzzle, ACTIONS
from .learning import encode


class HelltakerEnv(gym.Env):
    metadata = {"render_modes": ["rgb_array"]}

    def __init__(self, scene=1, connection=DEFAULT_CONNECTION, max_steps=128):
        self.client = Client(connection)
        self.scene = scene
        self.max_steps = max_steps
        self.action_space = spaces.Discrete(4)
        initial = self.client.reset(scene)
        puzzle = Puzzle(initial)
        size = encode(puzzle, puzzle.initial).shape
        self.observation_space = spaces.Box(0, 1, shape=size, dtype=np.float32)
        self.steps = 0

    def _encode(self, observation):
        puzzle = Puzzle(observation)
        return encode(puzzle, puzzle.initial)

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        self.steps = 0
        observation = self.client.reset(self.scene, 0 if seed is None else seed)
        return self._encode(observation), {"state": observation}

    def step(self, action):
        response = self.client.step(ACTIONS[int(action)])
        observation = response["observation"]
        self.steps += 1
        terminated = bool(observation["goal_reached"] or observation["dead"])
        return self._encode(observation), response["reward"], terminated, self.steps >= self.max_steps and not terminated, {"state": observation}

    def render(self):
        from PIL import Image
        from io import BytesIO
        import base64
        data = self.client.request("render")["png_base64"]
        return np.asarray(Image.open(BytesIO(base64.b64decode(data))).convert("RGB"))

    def close(self):
        self.client.release()
