using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        private static int          _processId;
        private static DateTime     _startedAtUtc;
        private static string       _registrationFilePath;

        // 主线程任务队列：后台线程投递，EditorApplication.update 消费
        private static readonly ConcurrentQueue<(Action work, ManualResetEventSlim gate)>
            _mainThreadQueue = new ConcurrentQueue<(Action, ManualResetEventSlim)>();

        static WucServer()
        {
            EditorApplication.update += DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
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
                    case "/identity":
                        result = HandleIdentity();
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

        private static object HandleIdentity()
        {
            return new
            {
                projectId = _projectId,
                projectPath = _projectPath,
                instanceId = _instanceId,
                pid = _processId,
                port = _boundPort,
                startedAtUtc = _startedAtUtc.ToString("O"),
            };
        }

        private static object HandleExecute(string body)
        {
            var req = JsonSerializer.Deserialize<ExecuteRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var r = OnMainThread(() => CSharpScriptRunner.Execute(
                req.code,
                req.scriptPath,
                req.timeoutMs > 0 ? req.timeoutMs : 30_000));

            // Keep execute responses concise: status, return value, logs, and timing.
            return new
            {
                success         = r.Success,
                returnValue     = BuildReturnValue(r.ReturnValue),
                error           = r.Error,
                logs            = r.Logs,
                executionTimeMs = r.ExecutionTimeMs,
            };
        }

        private static object BuildReturnValue(object value)
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
                timestamp  = e.Timestamp.ToString("HH:mm:ss.fff"),
                type       = e.Type.ToString(),
                message    = e.Message,
                stackTrace = e.StackTrace,
            }).ToList();
        }

        private static object HandleClearLogs()
        {
            CSharpScriptRunner.ClearLogBuffer();
            return new { ok = true };
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

        private class ExecuteRequest
        {
            public string code       { get; set; }
            public string scriptPath { get; set; }
            public int    timeoutMs  { get; set; } = 30_000;
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
