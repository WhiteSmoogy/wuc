# Wuc

**Wuc** is a Unity Editor HTTP server that lets AI agents (like Claude Code) inspect scenes, run C# code, and read logs — without any manual interaction with the Editor.

## How It Works

```
Claude Code
    │  (wuc skill + ~/.wuc/instances discovery)
    │  HTTP  127.0.0.1:<dynamic-port>
    ▼
WucServer.cs  [InitializeOnLoad]
    │  main-thread dispatch
    ▼
CSharpScriptRunner.cs  (Roslyn)
    ▼
Unity Engine / Editor API
```

The server starts automatically when Unity loads the project. Claude Code talks to it via the bundled skill.

## Setup

### 1. Install Wuc into your Unity project

**Option A — Unity Package (recommended)**

Import `UnityPackages/Wuc.unitypackage` into your project:
_Assets → Import Package → Custom Package…_ and select the file.

**Option B — Copy the source**

Copy `Assets/Editor/Wuc/` into your own project's `Assets/Editor/` folder.

After importing, the Wuc HTTP server starts automatically — confirm in the Console:

```
[Wuc] Server listening on http://127.0.0.1:<port>/ (projectId=..., instanceId=...)
```

By default Wuc scans an available port in the range `23557-23657`.
You can configure range (and optional `projectId` override) in `ProjectSettings/WucSettings.asset`.
The skill auto-reads this `projectId` override when present.


### Native runtime build (Rust + DllImport)

The daemon runtime is now implemented in Rust (`native/wuc_daemon_runtime`) and loaded via C# `DllImport("wuc_daemon_runtime")`.
Build the dynamic library and place it under `Assets/Editor/Wuc/Plugins/` with platform naming:

- macOS: `libwuc_daemon_runtime.dylib`
- Linux: `libwuc_daemon_runtime.so`
- Windows: `wuc_daemon_runtime.dll`

Example:

```bash
cd native/wuc_daemon_runtime
cargo build --release
```

Then copy the produced artifact from `target/release/` into `Assets/Editor/Wuc/Plugins/`.

### 2. Install the Claude Code skill

Copy the skill into your user-level Claude skills directory so Claude Code can find it:

```bash
# macOS / Linux
cp -r .claude/skills/wuc ~/.claude/skills/wuc

# Windows (PowerShell)
Copy-Item -Recurse .claude\skills\wuc $env:USERPROFILE\.claude\skills\wuc
```

### 3. Use the skill in Claude Code

Open Claude Code in any project directory and talk to Unity naturally:

```
query the scene hierarchy
run this C# snippet in Unity: return Camera.main.transform.position;
show me the last 20 Unity logs
```

Claude Code will invoke the `wuc` skill automatically when you ask it to interact with Unity.

> Note: `.claude/skills/wuc/wuc.py` is only a one-shot CLI client. It is **not** the native core and does not provide persistent polling/hosting. The nativeization target is a long-lived out-of-process daemon (Rust/C++/Go) that owns the stable endpoint.

## HTTP API (dynamic port)

The skill discovers Unity via `~/.wuc/instances/*.json`, verifies identity via `/identity`,
then calls the HTTP API:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/identity` | GET | Return `{ projectId, projectPath, instanceId, pid, port, startedAtUtc }` |
| `/health` | GET | Return readiness + boot identity for reconnect logic |
| `/execute` | POST | Run C# code on the Unity main thread |
| `/logs` | GET | Fetch recent log entries (`?count=N`, default 100) from `Temp/wuc.log` |
| `/logs/clear` | POST | Clear all entries from `Temp/wuc.log` |
| `/logs/clear-before` | POST | Remove entries earlier than a given timestamp |
| `/daemon/dispatch` | POST | Transport-agnostic bridge for native daemon command forwarding |
| `/daemon/drain` | POST | Drain buffered daemon commands (for BufferIfUnready mode) |
| `/daemon/queue` | GET | Queue depth/state for daemon runtime |

Use `/logs/clear` when an agent needs a truly clean slate before deciding whether a fresh compile produced errors. Use `/logs/clear-before` for incremental consumption after recording a `timestampUtc` from `/logs`.

### `/logs/clear-before` request

```json
{ "before": "2026-03-08T12:34:56.789Z" }
```

### `/execute` request

```json
{ "code": "return Application.unityVersion;", "scriptPath": "label.csx", "timeoutMs": 30000, "requestId": "uuid" }
```

### `/execute` response

```json
{
  "success": true,
  "requestId": "uuid",
  "returnValue": { "type": "System.String", "content": "6000.0.60f1" },
  "logs": [],
  "executionTimeMs": 42.3,
  "error": null
}
```

`requestId` can be used as an idempotency key. Re-sending the same `requestId` shortly after a transient disconnect (for example during domain reload) will return the cached first result instead of re-running side effects.



### `/daemon/dispatch` request

```json
{
  "path": "/execute",
  "body": "{\"code\":\"return Application.isPlaying;\",\"requestId\":\"cmd-1\"}",
  "mode": "buffer_if_unready",
  "requestId": "cmd-1",
  "requestClass": "must-run-once"
}
```

- `mode` supports:
  - `reject_if_unready` (default): return retryable error during reload.
  - `buffer_if_unready`: enqueue command and execute later via `/daemon/drain`.

## C# Scripting

Scripts run via Roslyn on the **Unity main thread**. The following namespaces are injected as default `using` statements into every script — agents do not need to add them explicitly:

- `System`, `System.Linq`, `System.Collections.Generic`
- `UnityEngine`, `UnityEditor`

All loaded assemblies are referenced automatically.

The last expression in the script is returned as `returnValue`.

## Key Files

| Path | Role |
|------|------|
| `Assets/Editor/Wuc/WucServer.cs` | HTTP server, route dispatch, main-thread queue |
| `Assets/Editor/Wuc/WucSettings.cs` | Project settings (port range and project ID override) |
| `Assets/Editor/Wuc/CSharpScriptRunner.cs` | Roslyn executor, append-only `Temp/wuc.log` persistence |
| `Assets/Editor/Wuc/Plugins/` | Roslyn DLLs + System.Text.Json (bundled) |
| `.claude/skills/wuc/` | Claude Code skill (copy to `~/.claude/skills/`) |

## Domain Reload Resilience (Native Core Exploration)

When Unity performs a domain reload, managed static state is rebuilt. Because `WucServer` currently lives in managed Editor code (`[InitializeOnLoad]`), there is a short control-plane downtime window while the listener restarts. This is especially visible for workflows like **start game** then immediately issuing follow-up commands.

Recommended direction (incremental):

1. **In-process native daemon host (current implementation direction)**
   - Native host can live inside Unity process (outside managed AppDomain), keeping transport/control-plane alive across C# domain reload.
   - Managed side is reduced to a command router + main-thread executor.
   - Wuc now exposes transport-agnostic command routing (`/daemon/dispatch`) so native host can forward core routes (`/execute`, `/logs`, `/identity`, `/health`) through one bridge.
2. **Phase 1 (implemented): protocol + state semantics**
   - `requestId` idempotency for `/execute`.
   - `/health` + daemon state (`ready`/`reloading`) and queue depth for deterministic retry logic.
3. **Phase 2 (implemented): daemon runtime + buffering**
   - Added daemon runtime state machine tied to Unity compile/reload lifecycle.
   - Added queueing via `/daemon/dispatch` (`buffer_if_unready`) and explicit draining via `/daemon/drain`.
4. **Phase 3 (implemented): core route bridging**
   - Core routes (`/execute`, `/logs`, `/identity`, `/health`, clear APIs) are routable through transport-agnostic daemon router.
   - Added `/daemon/queue` operational introspection endpoint.

This keeps managed Unity code focused on main-thread execution while letting an in-process native host own the control-plane behavior.
