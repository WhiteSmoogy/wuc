using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace Wuc
{
    internal enum WucDaemonState
    {
        Ready,
        Reloading,
    }

    internal enum WucDispatchMode
    {
        RejectIfUnready,
        BufferIfUnready,
    }

    internal static class WucDaemonRuntime
    {
        private const int MaxQueueSize = 256;
        private static bool _nativeReady;
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            _nativeReady = WucNativeDaemonBridge.TryInit(MaxQueueSize);
            if (!_nativeReady)
            {
                UnityEngine.Debug.LogWarning("[Wuc] Native daemon runtime not found (wuc_daemon_runtime). /daemon buffering disabled.");
                return;
            }

            WucNativeDaemonBridge.SetState(EditorApplication.isCompiling ? WucDaemonState.Reloading : WucDaemonState.Ready);
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        internal static void Shutdown()
        {
            if (!_initialized)
                return;

            _initialized = false;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;

            if (_nativeReady)
                WucNativeDaemonBridge.Shutdown();

            _nativeReady = false;
        }

        internal static string CurrentState => !_nativeReady
            ? "reloading"
            : (WucNativeDaemonBridge.CurrentState() == WucDaemonState.Ready ? "ready" : "reloading");

        internal static int QueueDepth => !_nativeReady ? 0 : WucNativeDaemonBridge.QueueDepth();

        internal static object DispatchOrQueue(
            string path,
            string body,
            string requestId,
            string requestClass,
            WucDispatchMode mode,
            Func<Func<object>, object> onMainThread)
        {
            if (!_nativeReady)
            {
                return new
                {
                    ok = false,
                    retryable = true,
                    state = "reloading",
                    error = "native daemon runtime unavailable",
                    requestId,
                    requestClass,
                };
            }

            if (WucNativeDaemonBridge.CurrentState() == WucDaemonState.Ready)
                return WucDaemonCommandRouter.Dispatch(path, body, onMainThread);

            if (mode == WucDispatchMode.BufferIfUnready)
            {
                var accepted = WucNativeDaemonBridge.Enqueue(path, body, requestId, requestClass);
                return new
                {
                    ok = accepted,
                    accepted,
                    buffered = accepted,
                    state = CurrentState,
                    queueDepth = QueueDepth,
                    requestId,
                    requestClass,
                    error = accepted ? null : "daemon queue is full or unavailable",
                };
            }

            return new
            {
                ok = false,
                retryable = true,
                state = CurrentState,
                error = "daemon runtime is reloading; retry later or use BufferIfUnready",
                requestId,
                requestClass,
            };
        }

        internal static object Drain(int maxCount, Func<Func<object>, object> onMainThread)
        {
            if (!_nativeReady)
            {
                return new
                {
                    ok = false,
                    state = "reloading",
                    error = "native daemon runtime unavailable",
                    drainedCount = 0,
                    queueDepth = 0,
                    drained = Array.Empty<object>(),
                };
            }

            if (maxCount <= 0)
                maxCount = 1;

            var drained = new List<object>();
            for (var i = 0; i < maxCount; i++)
            {
                var json = WucNativeDaemonBridge.DequeueJson();
                if (string.IsNullOrEmpty(json))
                    break;

                DaemonQueuedCommand cmd;
                try
                {
                    cmd = JsonSerializer.Deserialize<DaemonQueuedCommand>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    drained.Add(new { ok = false, error = $"invalid queued payload: {ex.Message}" });
                    continue;
                }

                try
                {
                    var result = WucDaemonCommandRouter.Dispatch(cmd.path, cmd.body, onMainThread);
                    drained.Add(new { ok = true, requestId = cmd.requestId, requestClass = cmd.requestClass, result });
                }
                catch (Exception ex)
                {
                    drained.Add(new { ok = false, requestId = cmd.requestId, requestClass = cmd.requestClass, error = ex.Message });
                }
            }

            return new
            {
                ok = true,
                state = CurrentState,
                drainedCount = drained.Count,
                queueDepth = QueueDepth,
                drained,
            };
        }

        internal static object QueueStats()
        {
            return new
            {
                state = CurrentState,
                queueDepth = QueueDepth,
                maxQueueSize = MaxQueueSize,
                nativeRuntimeLoaded = _nativeReady,
            };
        }

        private static void OnCompilationStarted(object obj)
        {
            if (_nativeReady)
                WucNativeDaemonBridge.SetState(WucDaemonState.Reloading);
        }

        private static void OnCompilationFinished(object obj)
        {
            if (_nativeReady)
                WucNativeDaemonBridge.SetState(WucDaemonState.Ready);
        }

        private class DaemonQueuedCommand
        {
            public string path { get; set; }
            public string body { get; set; }
            public string requestId { get; set; }
            public string requestClass { get; set; }
        }
    }
}
