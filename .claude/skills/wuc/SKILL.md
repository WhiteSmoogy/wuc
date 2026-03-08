---
name: wuc
description: Interact with the running Unity Editor via the Wuc HTTP server (discovered via ~/.wuc/instances).
allowed-tools: Bash(python *)
---

# Unity Editor via Wuc

## Quick start

```bash
# check Unity is reachable and see recent logs
python {skillDir}/wuc/wuc.py logs --count 10

# print selected Unity instance identity
python {skillDir}/wuc/wuc.py identity

# run a C# expression, get the return value
python {skillDir}/wuc/wuc.py execute "return Application.unityVersion;"

# list all GameObjects in the active scene
python {skillDir}/wuc/wuc.py execute "return Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Select(o => o.name).ToList();"

# clear all history in Temp/wuc.log
python {skillDir}/wuc/wuc.py clear

# clear only logs earlier than a timestamp
python {skillDir}/wuc/wuc.py clear-before "2026-03-08T12:34:56.789Z"
```

## Commands

### Execute C# code

```bash
python {skillDir}/wuc/wuc.py execute "<C# code>"
python {skillDir}/wuc/wuc.py execute "<C# code>" --path "Label.csx"
python {skillDir}/wuc/wuc.py execute "<C# code>" --timeout 10000
python {skillDir}/wuc/wuc.py --instance-id "<instanceId>" execute "<C# code>"
```

- `--path` — virtual filename shown in stack traces (cosmetic only)
- `--timeout` — execution timeout in milliseconds (default: 30000)
- `--instance-id` — connect to a specific Unity instance when multiple editors are open

Response fields:

| Field | Type | Description |
|-------|------|-------------|
| `success` | bool | Whether execution succeeded |
| `returnValue` | object\|null | `{"type": "<FullTypeName>", "content": <JSON value>}`, or `null` if the script returns null. `content` is the JSON-serialized value; falls back to a string if the type is not JSON-serializable. |
| `logs` | string[] | Unity log messages captured during execution |
| `executionTimeMs` | number | Wall-clock time in ms |
| `error` | string | Error message + stack trace on failure |

### Get logs

```bash
python wuc.py logs                # last 100 entries
python wuc.py logs --count 50
```

Each entry: `timestamp` (HH:mm:ss.fff), `timestampUtc` (ISO 8601 UTC), `type` (Log/Warning/Error/Assert/Exception), `message`, `stackTrace`.

Logs are appended to `Temp/wuc.log` as JSON lines and `/logs` reads the most recent entries from that file.

### Clear all log history

```bash
python wuc.py clear
```

`clear` empties `Temp/wuc.log`. Use this before asking an agent to judge whether a fresh compile produced errors.

### Clear logs before a timestamp

```bash
python wuc.py clear-before "2026-03-08T12:34:56.789Z"
```

`clear-before` removes only entries earlier than the provided timestamp and keeps newer logs intact. The safest input is the `timestampUtc` value returned by `python wuc.py logs`.

### Get selected instance identity

```bash
python wuc.py identity
python wuc.py --instance-id "<instanceId>" identity
```

Returns: `{ projectId, projectPath, instanceId, pid, port, startedAtUtc }`.

## Scripting context

Scripts run via Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`) on the Unity main thread.

**Auto-imported namespaces** — no `using` needed:
- `System`, `System.Linq`, `System.Collections.Generic`
- `UnityEngine`, `UnityEditor`

**All loaded assemblies** are referenced automatically.

**Return value:** the last expression in the script is returned as `returnValue`, a JSON object `{"type": "<FullTypeName>", "content": <value>}`. For example, a `string` result yields `{"type": "System.String", "content": "hello"}`, and a `Vector3` yields `{"type": "UnityEngine.Vector3", "content": {"x":1.0,"y":0.0,"z":0.0}}`.

## Notes

- Unity Editor must be open with the Wuc project loaded. The server starts automatically via `[InitializeOnLoad]`.
- Unity registers each running editor in `~/.wuc/instances/<instanceId>.json`; the client matches by `projectId` and verifies with `/identity`.
- `projectId` defaults to a hash of the current project path, and automatically uses `ProjectSettings/WucSettings.asset` override when configured.
- If multiple Unity editors are running for the same project, commands fail fast unless `--instance-id` is provided.
- All scripts run on the Unity main thread — avoid blocking calls (e.g. `Thread.Sleep`).
- If Unity is not running, the command exits immediately with a connection error.
- Run all commands from `D:\Projects\Wuc`.

## Example: inspect a GameObject

```bash
python {skillDir}/wuc/wuc.py execute "
var cam = GameObject.Find(\"Main Camera\");
if (cam == null) return null;
var t = cam.transform;
return new { position = t.position, rotation = t.eulerAngles };
"
```

## Example: query scene hierarchy

```bash
python {skillDir}/wuc/wuc.py execute "
var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
return roots.Select(r => new {
    name = r.name,
    children = r.transform.Cast<Transform>().Select(c => c.name).ToList()
}).ToList();
"
```

## Example: watch logs during play mode

```bash
python {skillDir}/wuc/wuc.py clear
# ... trigger action in Unity ...
python {skillDir}/wuc/wuc.py logs --count 20
```
