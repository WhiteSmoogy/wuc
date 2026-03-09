using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Wuc
{
    [InitializeOnLoad]
    public static class WucServer
    {
        private static HttpListener _listener;
        private static Thread       _listenerThread;
        private static int          _boundPort;
        private static string       _projectId;
        private static string       _projectPath;
        private static string       _instanceId;
        private static readonly string _processBootId = Guid.NewGuid().ToString("N");
        private static int          _processId;
        private static DateTime     _startedAtUtc;
        private static string       _registrationFilePath;

        // 主线程任务队列：后台线程投递，EditorApplication.update 消费
        private static readonly ConcurrentQueue<(Action work, ManualResetEventSlim gate)>
            _mainThreadQueue = new ConcurrentQueue<(Action, ManualResetEventSlim)>();

        // requestId 去重缓存：同一个 requestId 短时间内重复调用时返回首次结果。
        private static readonly object _executeCacheLock = new object();
        private static readonly Dictionary<string, ExecuteCacheEntry> _executeCache =
            new Dictionary<string, ExecuteCacheEntry>();
        private static readonly TimeSpan _executeCacheTtl = TimeSpan.FromMinutes(2);

        static WucServer()
        {
            EditorApplication.update += DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            WucDaemonRuntime.Initialize();
            StartServer();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private static void StartServer()
        {
            if (_listener != null && _listener.IsListening)
                return;

            try
            {
                var settings = WucSettings.LoadOrCreate();
                _projectPath = WucSettings.NormalizeProjectPath(Path.Combine(Application.dataPath, ".."));
                _projectId = settings.ResolveProjectId(_projectPath);
                _instanceId = Guid.NewGuid().ToString("N");
                _processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                _startedAtUtc = DateTime.UtcNow;

                if (!TryBindListener(settings.PortRangeStart, settings.PortRangeEnd))
                {
                    Debug.LogError(
                        $"[Wuc] Failed to start server in port range " +
                        $"{settings.PortRangeStart}-{settings.PortRangeEnd}.");
                    return;
                }

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name         = "WucHttpServer",
                };
                _listenerThread.Start();

                WriteRegistrationFile();
                Debug.Log(
                    $"[Wuc] Server listening on http://127.0.0.1:{_boundPort}/ " +
                    $"(projectId={_projectId}, instanceId={_instanceId})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Wuc] Failed to start server: {ex.Message}");
            }
        }

        private static void Shutdown()
        {
            EditorApplication.update -= DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            _listener?.Close();
            _listener = null;
            _boundPort = 0;
            RemoveRegistrationFile();
            WucDaemonRuntime.Shutdown();
        }

        // ── 主线程调度 ──────────────────────────────────────────────────────

        private static void DrainMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var item))
            {
                try     { item.work(); }
                catch   { /* 异常在 work 内部已捕获 */ }
                finally { item.gate.Set(); }
            }
        }

        // 将 func 投递到主线程执行，阻塞等待结果（最多 35 s）
        private static T OnMainThread<T>(Func<T> func)
        {
            T         result = default;
            Exception caught = null;
            var       gate   = new ManualResetEventSlim(false);

            _mainThreadQueue.Enqueue((() =>
            {
                try     { result = func(); }
                catch (Exception ex) { caught = ex; }
            }, gate));

            if (!gate.Wait(35_000))
                throw new TimeoutException("Main thread dispatch timed out.");

            if (caught != null)
                ExceptionDispatchInfo.Capture(caught).Throw();

            return result;
        }

        // ── HTTP 监听循环 ───────────────────────────────────────────────────

        private static void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;

            try
            {
                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();

                var path = req.Url.AbsolutePath.TrimEnd('/');

                object result;
                switch (path)
                {
                    case "/health":
                        result = BuildHealthPayload();
                        break;
                    case "/identity":
                        result = BuildIdentityPayload();
                        break;
                    case "/execute":
                        result = HandleExecute(body);
                        break;
                    case "/logs":
                        result = HandleGetLogs(req.QueryString["count"]);
                        break;
                    case "/logs/clear":
                        result = HandleClearLogs();
                        break;
                    case "/logs/clear-before":
                        result = HandleClearLogsBefore(body);
                        break;
                    case "/daemon/dispatch":
                        result = HandleDaemonDispatch(body);
                        break;
                    case "/daemon/drain":
                        result = HandleDaemonDrain(body);
                        break;
                    case "/daemon/queue":
                        result = WucDaemonRuntime.QueueStats();
                        break;
                    default:
                        resp.StatusCode = 404;
                        WriteJson(resp, new { error = "Not found" });
                        return;
                }

                resp.StatusCode = 200;
                WriteJson(resp, result);
            }
            catch (Exception ex)
            {
                try
                {
                    resp.StatusCode = 500;
                    WriteJson(resp, new { error = ex.Message, detail = ex.StackTrace });
                }
                catch { }
            }
            finally
            {
                resp.Close();
            }
        }

        // ── Route handlers ─────────────────────────────────────────────────

        internal static object BuildIdentityPayload()
        {
            return new
            {
                projectId = _projectId,
                projectPath = _projectPath,
                instanceId = _instanceId,
                pid = _processId,
                port = _boundPort,
                startedAtUtc = _startedAtUtc.ToString("O"),
                processBootId = _processBootId,
            };
        }

        internal static object BuildHealthPayload()
        {
            return new
            {
                status = WucDaemonRuntime.CurrentState,
                projectId = _projectId,
                instanceId = _instanceId,
                processBootId = _processBootId,
                startedAtUtc = _startedAtUtc.ToString("O"),
                daemonState = WucDaemonRuntime.CurrentState,
                queueDepth = WucDaemonRuntime.QueueDepth,
            };
        }

        private static object HandleExecute(string body)
        {
            var req = JsonSerializer.Deserialize<ExecuteRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (req == null || string.IsNullOrWhiteSpace(req.code))
                throw new ArgumentException("Missing 'code'.");

            return ExecuteWithIdempotency(req, () => OnMainThread(() => CSharpScriptRunner.Execute(
                req.code,
                req.scriptPath,
                req.timeoutMs > 0 ? req.timeoutMs : 30_000)));
        }

        internal static object ExecuteWithIdempotency(ExecuteRequest req, Func<ExecutionResult> execute)
        {
            CleanupExecuteCache();
            var requestId = string.IsNullOrWhiteSpace(req.requestId)
                ? null
                : req.requestId.Trim();

            if (!string.IsNullOrEmpty(requestId) && TryGetCachedExecuteResult(requestId, out var cached))
                return cached;

            var r = execute();

            var response = new
            {
                success         = r.Success,
                returnValue     = BuildReturnValue(r.ReturnValue),
                error           = r.Error,
                logs            = r.Logs,
                executionTimeMs = r.ExecutionTimeMs,
                requestId,
            };

            if (!string.IsNullOrEmpty(requestId))
                CacheExecuteResult(requestId, response);

            return response;
        }

        private static bool TryGetCachedExecuteResult(string requestId, out object response)
        {
            lock (_executeCacheLock)
            {
                if (_executeCache.TryGetValue(requestId, out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
                {
                    response = entry.Response;
                    return true;
                }
            }

            response = null;
            return false;
        }

        private static void CacheExecuteResult(string requestId, object response)
        {
            lock (_executeCacheLock)
            {
                _executeCache[requestId] = new ExecuteCacheEntry
                {
                    Response = response,
                    ExpiresAtUtc = DateTime.UtcNow.Add(_executeCacheTtl),
                };
            }
        }

        private static void CleanupExecuteCache()
        {
            var now = DateTime.UtcNow;
            lock (_executeCacheLock)
            {
                var expiredKeys = _executeCache
                    .Where(kv => kv.Value.ExpiresAtUtc <= now)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in expiredKeys)
                    _executeCache.Remove(key);
            }
        }

        internal static object BuildReturnValue(object value)
        {
            if (value == null) return null;

            var typeName = value.GetType().FullName;
            object content;
            try
            {
                var json = JsonSerializer.Serialize(value);
                using var doc = JsonDocument.Parse(json);
                content = doc.RootElement.Clone();
            }
            catch
            {
                content = value.ToString();
            }

            return new { type = typeName, content };
        }

        private static object HandleGetLogs(string countStr)
        {
            var count   = int.TryParse(countStr, out var n) ? n : 100;
            var entries = CSharpScriptRunner.GetRecentLogs(count);

            return entries.Select(e => new
            {
                timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
                timestampUtc = e.Timestamp.ToUniversalTime().ToString("O"),
                type = e.Type.ToString(),
                message = e.Message,
                stackTrace = e.StackTrace,
            }).ToList();
        }

        private static object HandleClearLogs()
        {
            var removedCount = CSharpScriptRunner.ClearLogBuffer();
            return new { ok = true, removedCount };
        }

        private static object HandleClearLogsBefore(string body)
        {
            var req = JsonSerializer.Deserialize<ClearLogsBeforeRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (req == null || string.IsNullOrWhiteSpace(req.before))
                throw new ArgumentException("Missing 'before' timestamp.");

            if (!DateTime.TryParse(
                    req.before,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var cutoff))
            {
                throw new ArgumentException(
                    "Invalid 'before' timestamp. Use ISO 8601, e.g. 2026-03-08T12:34:56.789Z.");
            }

            var removedCount = CSharpScriptRunner.ClearLogsBefore(cutoff);
            return new
            {
                ok = true,
                before = cutoff.ToUniversalTime().ToString("O"),
                removedCount,
            };
        }

        private static object HandleDaemonDispatch(string body)
        {
            var req = JsonSerializer.Deserialize<DaemonDispatchRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (req == null || string.IsNullOrWhiteSpace(req.path))
                throw new ArgumentException("Missing 'path'.");

            var mode = ParseDispatchMode(req.mode);
            return WucDaemonRuntime.DispatchOrQueue(
                req.path,
                req.body ?? string.Empty,
                req.requestId,
                req.requestClass,
                mode,
                func => OnMainThread(func));
        }

        private static object HandleDaemonDrain(string body)
        {
            var req = JsonSerializer.Deserialize<DaemonDrainRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DaemonDrainRequest();

            return WucDaemonRuntime.Drain(req.maxCount <= 0 ? 32 : req.maxCount, func => OnMainThread(func));
        }

        private static WucDispatchMode ParseDispatchMode(string mode)
        {
            if (string.Equals(mode, "buffer_if_unready", StringComparison.OrdinalIgnoreCase))
                return WucDispatchMode.BufferIfUnready;
            return WucDispatchMode.RejectIfUnready;
        }

        // ── Listener + registry ────────────────────────────────────────────

        private static bool TryBindListener(int startPort, int endPort)
        {
            for (var port = startPort; port <= endPort; port++)
            {
                if (TryStartListener(port))
                {
                    _boundPort = port;
                    return true;
                }
            }

            return false;
        }

        private static bool TryStartListener(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                return true;
            }
            catch
            {
                try { listener.Close(); } catch { }
                return false;
            }
        }

        private static void WriteRegistrationFile()
        {
            try
            {
                var registryDir = GetRegistryDirectory();
                Directory.CreateDirectory(registryDir);

                _registrationFilePath = Path.Combine(registryDir, $"{_instanceId}.json");
                var tempFilePath = _registrationFilePath + ".tmp";

                var payload = new RegistrationRecord
                {
                    projectId = _projectId,
                    projectPath = _projectPath,
                    instanceId = _instanceId,
                    pid = _processId,
                    port = _boundPort,
                    startedAtUtc = _startedAtUtc.ToString("O"),
                    updatedAtUtc = DateTime.UtcNow.ToString("O"),
                };

                var json = JsonSerializer.Serialize(payload);
                File.WriteAllText(tempFilePath, json, Encoding.UTF8);

                if (File.Exists(_registrationFilePath))
                {
                    try { File.Delete(_registrationFilePath); } catch { }
                }
                File.Move(tempFilePath, _registrationFilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Wuc] Failed to write registry file: {ex.Message}");
            }
        }

        private static void RemoveRegistrationFile()
        {
            if (string.IsNullOrEmpty(_registrationFilePath))
                return;

            try
            {
                if (File.Exists(_registrationFilePath))
                    File.Delete(_registrationFilePath);
            }
            catch { }
            finally
            {
                _registrationFilePath = null;
            }
        }

        private static string GetRegistryDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".wuc", "instances");
        }

        // ── JSON helper ────────────────────────────────────────────────────

        private static void WriteJson(HttpListenerResponse resp, object obj)
        {
            var json  = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentType     = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
        }

        // ── Request DTO ────────────────────────────────────────────────────

        internal class ExecuteRequest
        {
            public string code       { get; set; }
            public string scriptPath { get; set; }
            public int    timeoutMs  { get; set; } = 30_000;
            public string requestId  { get; set; }
        }

        private class ExecuteCacheEntry
        {
            public object Response { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        internal class ClearLogsBeforeRequest
        {
            public string before { get; set; }
        }

        private class DaemonDispatchRequest
        {
            public string path { get; set; }
            public string body { get; set; }
            public string mode { get; set; }
            public string requestId { get; set; }
            public string requestClass { get; set; }
        }

        private class DaemonDrainRequest
        {
            public int maxCount { get; set; } = 32;
        }

        private class RegistrationRecord
        {
            public string projectId { get; set; }
            public string projectPath { get; set; }
            public string instanceId { get; set; }
            public int pid { get; set; }
            public int port { get; set; }
            public string startedAtUtc { get; set; }
            public string updatedAtUtc { get; set; }
        }
    }
}
