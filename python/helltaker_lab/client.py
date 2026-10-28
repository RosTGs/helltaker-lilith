"""A single request maps to one serialized operation on Unity's main thread."""
import base64
import json
import os
from pathlib import Path
import socket


_local_config = Path(__file__).resolve().parents[2] / "local-config.json"
_local_settings = json.loads(_local_config.read_text(encoding="utf-8")) if _local_config.exists() else {}
DEFAULT_CONNECTION = Path(os.environ.get("HELLTAKER_CONNECTION",
    _local_settings.get("connection", str(Path.cwd() / "HelltakerAILab/connection.json"))))


class BridgeError(RuntimeError):
    pass


class Client:
    def __init__(self, connection=DEFAULT_CONNECTION, timeout=32):
        self.connection = Path(connection)
        self.timeout = timeout

    def request(self, command, **parameters):
        connection = json.loads(self.connection.read_text(encoding="utf-8-sig"))
        payload = dict(command=command, token=connection["token"], **parameters)
        with socket.create_connection((connection["host"], connection["port"]), self.timeout) as sock:
            sock.settimeout(self.timeout)
            sock.sendall(json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\n")
            with sock.makefile("rb") as stream:
                raw = stream.readline(32 * 1024 * 1024)
        if not raw:
            raise BridgeError("Start Helltaker with the AI Lab mod and retry")
        result = json.loads(raw)
        if not result.get("ok"):
            raise BridgeError(f"{result.get('error')}: {result.get('message')}")
        return result

    def observe(self):
        return self.request("observe")["observation"]

    def scenes(self):
        return self.request("scenes")["scenes"]

    def reset(self, scene=None, seed=0):
        params = dict(seed=seed)
        if scene is not None:
            params["scene"] = scene
        return self.request("reset", **params)["observation"]

    def step(self, action, seconds=0.35, hold_frames=1):
        return self.request("step", action=action, seconds=seconds, hold_frames=hold_frames)

    def render(self, output):
        output = Path(output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(base64.b64decode(self.request("render")["png_base64"]))
        return output

    def release(self):
        return self.request("release")


def compact(observation):
    return {key: observation[key] for key in (
        "scene", "episode", "step", "phase", "ready", "player", "dialogues", "events", "goal_reached", "dead")}
