#!/usr/bin/env python3
"""Wuc Unity skill — one-shot HTTP client for the Wuc Unity Editor server."""

import sys
import json
import argparse
import hashlib
import os
import uuid
from pathlib import Path
import urllib.request
import urllib.error

DEFAULT_LEGACY_PORT = 23557
REGISTRY_DIR = Path.home() / ".wuc" / "instances"


class WucError(RuntimeError):
    """Handled error for user-facing CLI output."""


def call(base, method, path, body=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    req  = urllib.request.Request(
        base + path,
        data=data,
        headers={"Content-Type": "application/json"} if data else {},
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")
        raise WucError(f"HTTP {e.code} from Unity: {detail}") from e
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", str(e))
        raise WucError(f"Cannot reach Unity: {reason}") from e


def normalize_project_path(path):
    normalized = os.path.normpath(os.path.abspath(path)).rstrip("\\/")
    if os.name == "nt":
        normalized = normalized.lower()
    return normalized


def build_project_id_from_path(path):
    digest = hashlib.sha256(normalize_project_path(path).encode("utf-8")).hexdigest()
    return f"sha256:{digest}"


def read_project_id_override(project_path):
    settings_path = Path(project_path) / "ProjectSettings" / "WucSettings.asset"
    if not settings_path.exists():
        return None

    try:
        for line in settings_path.read_text(encoding="utf-8").splitlines():
            marker = "_projectIdOverride:"
            if marker not in line:
                continue
            _, _, raw_value = line.partition(marker)
            value = raw_value.strip().strip('"').strip("'")
            return value or None
    except OSError:
        return None

    return None


def resolve_project_id(project_path, explicit_project_id):
    if explicit_project_id:
        return explicit_project_id

    override = read_project_id_override(project_path)
    if override:
        return override

    return build_project_id_from_path(project_path)


def load_registry_records(project_id):
    if not REGISTRY_DIR.exists():
        return []

    records = []
    for file_path in REGISTRY_DIR.glob("*.json"):
        try:
            data = json.loads(file_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue

        if data.get("projectId") != project_id:
            continue

        data["_registryFile"] = file_path
        records.append(data)

    return records


def verify_candidate(record, expected_project_id):
    port = record.get("port")
    instance_id = record.get("instanceId")
    if not isinstance(port, int) or port <= 0 or not isinstance(instance_id, str):
        return None

    base = f"http://127.0.0.1:{port}"
    try:
        identity = call(base, "GET", "/identity", timeout=2)
    except WucError:
        return None

    if identity.get("projectId") != expected_project_id:
        return None
    if identity.get("instanceId") != instance_id:
        return None

    return base, identity


def cleanup_stale_records(stale_records):
    for record in stale_records:
        path = record.get("_registryFile")
        if not isinstance(path, Path):
            continue
        try:
            path.unlink(missing_ok=True)
        except OSError:
            pass


def pick_target_base(project_id, requested_instance_id):
    records = load_registry_records(project_id)

    if requested_instance_id:
        records = [r for r in records if r.get("instanceId") == requested_instance_id]
        if not records:
            raise WucError(
                f"Instance '{requested_instance_id}' not found for projectId {project_id} "
                f"in {REGISTRY_DIR}"
            )

    verified = []
    stale = []
    for record in records:
        resolved = verify_candidate(record, project_id)
        if resolved is None:
            stale.append(record)
            continue
        verified.append((record, resolved[0], resolved[1]))

    cleanup_stale_records(stale)

    if len(verified) == 1:
        return verified[0][1]

    if len(verified) > 1:
        if requested_instance_id:
            return verified[0][1]

        lines = [
            "Multiple Unity instances are running for this project; "
            "specify --instance-id to avoid connecting to the wrong one:"
        ]
        for rec, _, identity in sorted(
            verified,
            key=lambda item: item[0].get("startedAtUtc", ""),
            reverse=True,
        ):
            lines.append(
                f"  - instanceId={identity.get('instanceId')} "
                f"pid={identity.get('pid')} port={identity.get('port')} "
                f"startedAtUtc={identity.get('startedAtUtc')}"
            )
        raise WucError("\n".join(lines))

    # Backward-compatible fallback for old single-port servers without registry.
    legacy_base = f"http://127.0.0.1:{DEFAULT_LEGACY_PORT}"
    try:
        identity = call(legacy_base, "GET", "/identity", timeout=2)
        if identity.get("projectId") == project_id:
            if requested_instance_id and identity.get("instanceId") != requested_instance_id:
                raise WucError(
                    f"Legacy server on port {DEFAULT_LEGACY_PORT} is not instance "
                    f"'{requested_instance_id}'."
                )
            return legacy_base
    except WucError:
        pass

    raise WucError(
        f"No reachable Unity instance found for projectId {project_id}. "
        f"Open Unity in this project or check {REGISTRY_DIR}."
    )


def main():
    ap  = argparse.ArgumentParser(prog="wuc", description=__doc__)
    ap.add_argument(
        "--project-id",
        default=None,
        help="Explicit projectId. Default: WucSettings override or sha256 hash of project path.",
    )
    ap.add_argument(
        "--project-path",
        default=None,
        help="Project path used to compute projectId (default: current working directory).",
    )
    ap.add_argument(
        "--instance-id",
        default=None,
        help="Connect to a specific Unity instanceId.",
    )
    sub = ap.add_subparsers(dest="action", required=True)

    # ── execute ──────────────────────────────────────────────────────────
    p = sub.add_parser("execute", help="Run C# code in Unity Editor")
    p.add_argument("code",
                   help="C# code to execute")
    p.add_argument("--path",    default=None,
                   help="Virtual script path shown in stack traces")
    p.add_argument("--timeout", type=int, default=30_000,
                   help="Execution timeout in ms (default: 30000)")
    p.add_argument(
        "--request-id",
        default=None,
        help="Idempotency key for execute. Default: auto-generated UUID.",
    )

    # ── logs ─────────────────────────────────────────────────────────────
    p = sub.add_parser("logs", help="Get recent Unity log entries")
    p.add_argument("--count", type=int, default=100,
                   help="Max entries to return (default: 100)")

    # ── clear / clear-before ────────────────────────────────────────────
    sub.add_parser("clear", help="Clear all log entries from Temp/wuc.log")
    p = sub.add_parser("clear-before", help="Remove log entries older than a timestamp")
    p.add_argument(
        "before",
        help="Delete logs earlier than this timestamp (ISO 8601 recommended, e.g. 2026-03-08T12:34:56.789Z)",
    )
    sub.add_parser("identity", help="Get the selected Unity instance identity")
    sub.add_parser("health", help="Get server readiness and boot id")

    args = ap.parse_args()
    project_path = args.project_path or os.getcwd()
    project_id = resolve_project_id(project_path, args.project_id)

    try:
        base = pick_target_base(project_id, args.instance_id)

        if args.action == "execute":
            request_id = args.request_id or str(uuid.uuid4())
            payload = {
                "code": args.code,
                "scriptPath": args.path,
                "timeoutMs": args.timeout,
                "requestId": request_id,
            }
            result = call(
                base,
                "POST",
                "/execute",
                payload,
                timeout=args.timeout / 1000 + 5,
            )

        elif args.action == "logs":
            result = call(base, "GET", f"/logs?count={args.count}", timeout=10)

        elif args.action == "clear":
            result = call(base, "POST", "/logs/clear", timeout=10)

        elif args.action == "clear-before":
            result = call(
                base,
                "POST",
                "/logs/clear-before",
                {"before": args.before},
                timeout=10,
            )

        elif args.action == "identity":
            result = call(base, "GET", "/identity", timeout=5)

        elif args.action == "health":
            result = call(base, "GET", "/health", timeout=5)

    except WucError as e:
        sys.exit(f"[wuc] {e}")

    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
