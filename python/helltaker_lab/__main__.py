import argparse
import json
from pathlib import Path
from .client import Client, DEFAULT_CONNECTION, compact


def main():
    parser = argparse.ArgumentParser(description="Helltaker AI Lab")
    parser.add_argument("--connection", default=str(DEFAULT_CONNECTION))
    sub = parser.add_subparsers(dest="command", required=True)
    observe = sub.add_parser("observe")
    observe.add_argument("--full", action="store_true")
    for name in ("scenes", "release", "catalog", "hierarchy", "reload_visuals", "visual_status", "localization_status", "shutdown"):
        sub.add_parser(name)
    reset = sub.add_parser("reset")
    reset.add_argument("scene", type=int)
    reset.add_argument("--seed", type=int, default=0)
    step = sub.add_parser("step")
    step.add_argument("action")
    step.add_argument("--seconds", type=float, default=0.35)
    render = sub.add_parser("render")
    render.add_argument("output")
    export = sub.add_parser("export_texture")
    export.add_argument("key")
    args = parser.parse_args()
    client = Client(args.connection)
    if args.command == "observe":
        result = client.observe()
        if not args.full:
            result = compact(result)
    elif args.command == "reset":
        result = compact(client.reset(args.scene, args.seed))
    elif args.command == "step":
        result = client.step(args.action, seconds=args.seconds)
        result["observation"] = compact(result["observation"])
    elif args.command == "render":
        result = {"path": str(client.render(args.output).resolve())}
    elif args.command == "export_texture":
        result = client.request(args.command, key=args.key)
    else:
        result = client.request(args.command)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
