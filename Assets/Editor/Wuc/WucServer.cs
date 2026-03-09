using System;
using System.Text.Json;
using UnityEditor;
using UnityEngine;

namespace Wuc
{
    [InitializeOnLoad]
    public static class WucServer
    {
        static WucServer()
        {
            WucDaemonRuntime.Initialize();
            LogNativeServerIdentity();
        }

        private static void LogNativeServerIdentity()
        {
            var identity = WucDaemonRuntime.GetIdentity();
            if (identity == null || identity.port <= 0)
                return;

            Debug.Log(
                $"[Wuc] Native server listening on http://127.0.0.1:{identity.port}/ " +
                $"(projectId={identity.projectId}, instanceId={identity.instanceId})");
        }

        internal static string DispatchCommandToJson(string path, string body)
        {
            switch ((path ?? string.Empty).TrimEnd('/'))
            {
                case "/execute":
                    return HandleExecute(body);
                default:
                    return BuildErrorResponseJson($"Unsupported managed route: {path}", ExtractRequestId(body));
            }
        }

        private static string HandleExecute(string body)
        {
            ExecuteRequest req;
            try
            {
                req = JsonSerializer.Deserialize<ExecuteRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return BuildErrorResponseJson($"Invalid execute payload: {ex.Message}", null);
            }

            if (req == null || string.IsNullOrWhiteSpace(req.code))
                return BuildErrorResponseJson("Missing 'code'.", req?.requestId);

            var result = CSharpScriptRunner.Execute(
                req.code,
                req.scriptPath,
                req.timeoutMs > 0 ? req.timeoutMs : 30_000);

            return BuildExecuteResponseJson(result, req.requestId);
        }

        internal static string BuildExecuteResponseJson(ExecutionResult result, string requestId)
        {
            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                returnValue = BuildReturnValue(result.ReturnValue),
                error = result.Error,
                logs = result.Logs,
                executionTimeMs = result.ExecutionTimeMs,
                requestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim(),
            });
        }

        internal static string BuildErrorResponseJson(string error, string requestId)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                returnValue = (object)null,
                error,
                logs = Array.Empty<string>(),
                executionTimeMs = 0d,
                requestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim(),
            });
        }

        internal static object BuildReturnValue(object value)
        {
            if (value == null)
                return null;

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

        private static string ExtractRequestId(string body)
        {
            try
            {
                var req = JsonSerializer.Deserialize<ExecuteRequest>(
                    body ?? string.Empty,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return req?.requestId;
            }
            catch
            {
                return null;
            }
        }

        internal sealed class ExecuteRequest
        {
            public string code { get; set; }
            public string scriptPath { get; set; }
            public int timeoutMs { get; set; } = 30_000;
            public string requestId { get; set; }
        }
    }
}
