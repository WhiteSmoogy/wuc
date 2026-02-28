# Wuc

**Wuc** is a Unity Editor HTTP server that lets AI agents (like Claude Code) inspect scenes, run C# code, and read logs — without any manual interaction with the Editor.

## How It Works

```
Claude Code
    │  (wuc skill)
    │  HTTP  127.0.0.1:23557
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
[Wuc] Server listening on http://127.0.0.1:23557/
```

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

## HTTP API (port 23557)

The skill communicates with Unity over a simple HTTP API. You can also call it directly:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/execute` | POST | Run C# code on the Unity main thread |
| `/logs` | GET | Fetch recent log entries (`?count=N`, default 100) |
| `/logs/clear` | POST | Clear the log buffer |

### `/execute` request

```json
{ "code": "return Application.unityVersion;", "scriptPath": "label.csx", "timeoutMs": 30000 }
```

### `/execute` response

```json
{
  "success": true,
  "returnValue": { "type": "System.String", "content": "6000.0.60f1" },
  "output": "",
  "logs": [],
  "executionTimeMs": 42.3,
  "error": null
}
```

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
| `Assets/Editor/Wuc/CSharpScriptRunner.cs` | Roslyn executor, persistent log buffer |
| `Assets/Editor/Wuc/Plugins/` | Roslyn DLLs + System.Text.Json (bundled) |
| `.claude/skills/wuc/` | Claude Code skill (copy to `~/.claude/skills/`) |
