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
        public const int Port = 23557;

        private static HttpListener _listener;
        private static Thread       _listenerThread;

        // 主线程任务队列：后台线程投递，EditorApplication.update 消费
        private static readonly ConcurrentQueue<(Action work, ManualResetEventSlim gate)>
            _mainThreadQueue = new ConcurrentQueue<(Action, ManualResetEventSlim)>();

        static WucServer()
        {
            EditorApplication.update += DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            StartServer();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private static void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name         = "WucHttpServer",
                };
                _listenerThread.Start();

                Debug.Log($"[Wuc] Server listening on http://127.0.0.1:{Port}/");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Wuc] Failed to start server: {ex.Message}");
            }
        }

        private static void Shutdown()
        {
            EditorApplication.update -= DrainMainThreadQueue;
            _listener?.Close();
            _listener = null;
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

        private static object HandleExecute(string body)
        {
            var req = JsonSerializer.Deserialize<ExecuteRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var r = OnMainThread(() => CSharpScriptRunner.Execute(
                req.code,
                req.scriptPath,
                req.timeoutMs > 0 ? req.timeoutMs : 30_000));

            return new
            {
                success         = r.Success,
                returnValue     = BuildReturnValue(r.ReturnValue),
                error           = r.Error,
                output          = r.Output,
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
    }
}
