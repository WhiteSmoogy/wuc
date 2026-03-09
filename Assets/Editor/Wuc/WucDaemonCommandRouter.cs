using System;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace Wuc
{
    /// <summary>
    /// Transport-agnostic command router intended for in-process native daemon integration.
    /// A native host can forward command payloads here without depending on HttpListener.
    /// </summary>
    internal static class WucDaemonCommandRouter
    {
        internal static object Dispatch(string path, string body, Func<Func<object>, object> onMainThread)
        {
            switch ((path ?? string.Empty).TrimEnd('/'))
            {
                case "/identity":
                    return WucServer.BuildIdentityPayload();

                case "/health":
                    return WucServer.BuildHealthPayload();

                case "/execute":
                    return HandleExecute(body, onMainThread);

                case "/logs":
                    return HandleGetLogs(body);

                case "/logs/clear":
                    return new { ok = true, removedCount = CSharpScriptRunner.ClearLogBuffer() };

                case "/logs/clear-before":
                    return HandleClearLogsBefore(body);

                default:
                    throw new ArgumentException($"Unknown route: {path}");
            }
        }

        private static object HandleExecute(string body, Func<Func<object>, object> onMainThread)
        {
            var req = JsonSerializer.Deserialize<WucServer.ExecuteRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (req == null || string.IsNullOrWhiteSpace(req.code))
                throw new ArgumentException("Missing 'code'.");

            return onMainThread(() => WucServer.ExecuteWithIdempotency(
                req,
                () => CSharpScriptRunner.Execute(
                    req.code,
                    req.scriptPath,
                    req.timeoutMs > 0 ? req.timeoutMs : 30_000)));
        }

        // Body format: { "count": 100 }
        private static object HandleGetLogs(string body)
        {
            var req = JsonSerializer.Deserialize<LogsRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var count = req?.count > 0 ? req.count : 100;
            return CSharpScriptRunner.GetRecentLogs(count).Select(e => new
            {
                timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
                timestampUtc = e.Timestamp.ToUniversalTime().ToString("O"),
                type = e.Type.ToString(),
                message = e.Message,
                stackTrace = e.StackTrace,
            }).ToList();
        }

        private static object HandleClearLogsBefore(string body)
        {
            var req = JsonSerializer.Deserialize<WucServer.ClearLogsBeforeRequest>(
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

        private class LogsRequest
        {
            public int count { get; set; } = 100;
        }
    }
}
