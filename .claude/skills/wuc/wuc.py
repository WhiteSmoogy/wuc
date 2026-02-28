#!/usr/bin/env python3
"""Wuc Unity skill — one-shot HTTP client for the Wuc Unity Editor server."""

import sys
import json
import argparse
import urllib.request
import urllib.error

PORT = 23557
BASE = f"http://127.0.0.1:{PORT}"


def call(method, path, body=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    req  = urllib.request.Request(
        BASE + path,
        data=data,
        headers={"Content-Type": "application/json"} if data else {},
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode())
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", str(e))
        sys.exit(f"[wuc] Cannot reach Unity on port {PORT}: {reason}")


def main():
    ap  = argparse.ArgumentParser(prog="wuc", description=__doc__)
    sub = ap.add_subparsers(dest="action", required=True)

    # ── execute ──────────────────────────────────────────────────────────
    p = sub.add_parser("execute", help="Run C# code in Unity Editor")
    p.add_argument("code",
                   help="C# code to execute")
    p.add_argument("--path",    default=None,
                   help="Virtual script path shown in stack traces")
    p.add_argument("--timeout", type=int, default=30_000,
                   help="Execution timeout in ms (default: 30000)")

    # ── logs ─────────────────────────────────────────────────────────────
    p = sub.add_parser("logs", help="Get recent Unity log entries")
    p.add_argument("--count", type=int, default=100,
                   help="Max entries to return (default: 100)")

    # ── clear ────────────────────────────────────────────────────────────
    sub.add_parser("clear", help="Clear the persistent log buffer")

    args = ap.parse_args()

    if args.action == "execute":
        result = call(
            "POST", "/execute",
            {"code": args.code, "scriptPath": args.path, "timeoutMs": args.timeout},
            timeout=args.timeout / 1000 + 5,   # HTTP timeout slightly longer than script timeout
        )

    elif args.action == "logs":
        result = call("GET", f"/logs?count={args.count}", timeout=10)

    elif args.action == "clear":
        result = call("POST", "/logs/clear", timeout=10)

    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
