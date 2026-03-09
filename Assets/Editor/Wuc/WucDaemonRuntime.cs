using System;
using System.Text.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Wuc
{
    internal enum WucDaemonState
    {
        Ready,
        Reloading,
    }

    internal static class WucDaemonRuntime
    {
        private const int MaxQueueSize = 256;
        private const int MaxCommandsPerUpdate = 8;

        private static bool _initialized;
        private static bool _nativeReady;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            _nativeReady = WucNativeDaemonBridge.TryInit(MaxQueueSize);
            if (!_nativeReady)
            {
                Debug.LogWarning("[Wuc] Native daemon runtime not found (wuc_daemon_runtime). Wuc control plane disabled.");
                return;
            }

            AttachManaged();

            EditorApplication.update += PumpCommands;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            EditorApplication.quitting += OnEditorQuitting;

            WucNativeDaemonBridge.SetState(EditorApplication.isCompiling ? WucDaemonState.Reloading : WucDaemonState.Ready);
        }

        internal static NativeIdentity GetIdentity()
        {
            var json = WucNativeDaemonBridge.IdentityJson();
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<NativeIdentity>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        internal static void RecordLog(LogEntry entry)
        {
            if (!_nativeReady)
                return;

            WucNativeDaemonBridge.RecordLog(entry);
        }

        private static void AttachManaged()
        {
            var settings = WucSettings.LoadOrCreate();
            var projectPath = WucSettings.NormalizeProjectPath(System.IO.Path.Combine(Application.dataPath, ".."));
            var projectId = settings.ResolveProjectId(projectPath);
            var nowUtc = DateTime.UtcNow.ToString("O");
            var port = WucNativeDaemonBridge.AttachManaged(
                projectId,
                projectPath,
                System.Diagnostics.Process.GetCurrentProcess().Id,
                settings.PortRangeStart,
                settings.PortRangeEnd,
                nowUtc,
                nowUtc);

            if (port <= 0)
            {
                _nativeReady = false;
                Debug.LogError("[Wuc] Native daemon runtime failed to start its HTTP server.");
            }
        }

        private static void PumpCommands()
        {
            if (!_nativeReady || WucNativeDaemonBridge.CurrentState() != WucDaemonState.Ready)
                return;

            for (var i = 0; i < MaxCommandsPerUpdate; i++)
            {
                var commandJson = WucNativeDaemonBridge.DequeueCommandJson();
                if (string.IsNullOrWhiteSpace(commandJson))
                    break;

                QueuedCommand command;
                try
                {
                    command = JsonSerializer.Deserialize<QueuedCommand>(
                        commandJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Wuc] Failed to parse daemon command: {ex.Message}");
                    continue;
                }

                if (command == null || command.commandId == 0)
                    continue;

                var responseJson = WucDaemonCommandRouter.DispatchToJson(command.path, command.body);
                WucNativeDaemonBridge.CompleteCommand(command.commandId, responseJson);
            }
        }

        private static void OnCompilationStarted(object _)
        {
            if (_nativeReady)
                WucNativeDaemonBridge.SetState(WucDaemonState.Reloading);
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_nativeReady)
                WucNativeDaemonBridge.SetState(WucDaemonState.Reloading);
        }

        private static void OnAfterAssemblyReload()
        {
            if (!_nativeReady)
                return;

            AttachManaged();
            WucNativeDaemonBridge.SetState(EditorApplication.isCompiling ? WucDaemonState.Reloading : WucDaemonState.Ready);
        }

        private static void OnEditorQuitting()
        {
            if (!_nativeReady)
                return;

            WucNativeDaemonBridge.Shutdown();
            _nativeReady = false;
        }

        internal sealed class NativeIdentity
        {
            public string projectId { get; set; }
            public string projectPath { get; set; }
            public string instanceId { get; set; }
            public string processBootId { get; set; }
            public int pid { get; set; }
            public int port { get; set; }
            public string startedAtUtc { get; set; }
            public string updatedAtUtc { get; set; }
            public string status { get; set; }
        }

        private sealed class QueuedCommand
        {
            public ulong commandId { get; set; }
            public string path { get; set; }
            public string body { get; set; }
        }
    }
}
