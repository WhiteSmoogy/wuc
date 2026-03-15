using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Wuc
{
    public struct LogEntry
    {
        public DateTime Timestamp;
        public LogType Type;
        public string Message;
        public string StackTrace;

        public override string ToString()
        {
            var prefix = Type == LogType.Error ? "[Error] "
                       : Type == LogType.Warning ? "[Warning] "
                       : Type == LogType.Assert ? "[Assert] "
                       : "";
            return $"[{Timestamp:HH:mm:ss.fff}] {prefix}{Message}";
        }
    }

    public class ScriptGlobals
    {
        public void print(object value) => Debug.Log(value?.ToString() ?? "null");
        public void log(object value) => Debug.Log(value);
    }

    public class ExecutionResult
    {
        public bool Success { get; set; }
        public object ReturnValue { get; set; }
        public string Error { get; set; }
        public List<string> Logs { get; set; }
        public double ExecutionTimeMs { get; set; }
    }

    [InitializeOnLoad]
    public static class CSharpScriptRunner
    {
        // ── Persistent log file ────────────────────────────────────────────
        private static readonly object _logFileLock = new object();
        private static readonly object _roslynLock = new object();
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly string LogFilePath = GetLogFilePath();
        private static bool _isShuttingDown;

        static CSharpScriptRunner()
        {
            EnsureLogFileDirectory();
            Application.logMessageReceivedThreaded += OnPersistentLogReceived;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
        }

        /// <summary>Returns the most recent <paramref name="count"/> Unity log entries.</summary>
        public static List<LogEntry> GetRecentLogs(int count = 100)
        {
            if (count <= 0)
                return new List<LogEntry>();

            lock (_logFileLock)
            {
                if (!File.Exists(LogFilePath))
                    return new List<LogEntry>();

                var recentEntries = new Queue<LogEntry>(count);
                foreach (var line in File.ReadLines(LogFilePath))
                {
                    if (!TryParsePersistedLogEntry(line, out var entry))
                        continue;

                    if (recentEntries.Count >= count)
                        recentEntries.Dequeue();

                    recentEntries.Enqueue(entry);
                }

                return recentEntries.ToList();
            }
        }

        /// <summary>Clears the persistent log file.</summary>
        public static int ClearLogBuffer()
        {
            lock (_logFileLock)
            {
                EnsureLogFileDirectory();

                var removedCount = File.Exists(LogFilePath)
                    ? File.ReadLines(LogFilePath).Count(line => !string.IsNullOrWhiteSpace(line))
                    : 0;

                File.WriteAllText(LogFilePath, string.Empty, Utf8WithoutBom);
                return removedCount;
            }
        }

        /// <summary>Removes log entries strictly earlier than <paramref name="cutoff"/>.</summary>
        public static int ClearLogsBefore(DateTime cutoff)
        {
            lock (_logFileLock)
            {
                EnsureLogFileDirectory();

                if (!File.Exists(LogFilePath))
                {
                    File.WriteAllText(LogFilePath, string.Empty, Utf8WithoutBom);
                    return 0;
                }

                var remainingLines = new List<string>();
                var removedCount = 0;

                foreach (var line in File.ReadAllLines(LogFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (TryParsePersistedLogEntry(line, out var entry) && entry.Timestamp < cutoff)
                    {
                        removedCount++;
                        continue;
                    }

                    remainingLines.Add(line);
                }

                File.WriteAllLines(LogFilePath, remainingLines, Utf8WithoutBom);
                return removedCount;
            }
        }

        private static void OnPersistentLogReceived(string condition, string stackTrace, LogType type)
        {
            if (_isShuttingDown)
                return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Type = type,
                Message = condition,
                StackTrace = stackTrace,
            };

            AppendLogEntry(entry);
            WucDaemonRuntime.RecordLog(entry);
        }

        private static void OnEditorQuitting()
        {
            _isShuttingDown = true;
            UnsubscribeLifecycleCallbacks();
        }

        // ── Roslyn state ───────────────────────────────────────────────────
        private static bool _roslynInitialized;
        private static string _roslynInitError;
        private static object _scriptOptions;

        private static Type _scriptOptionsType;
        private static Type _csharpScriptType;
        private static MethodInfo _cachedEvaluateSourceTextMethod;
        private static MethodInfo _cachedEvaluateStringMethod;
        private static MetadataReference[] _cachedMetadataReferences;

        private static void OnBeforeAssemblyReload()
        {
            Debug.Log("Assembly reload detected, clearing Roslyn state.");
            UnsubscribeLifecycleCallbacks();
            DisposeRoslynState();
        }

        private static void UnsubscribeLifecycleCallbacks()
        {
            Application.logMessageReceivedThreaded -= OnPersistentLogReceived;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
        }

        private static void OnCompilationStarted(object context)
        {
            Debug.Log($"Compilation started, clearing Roslyn state. {context}");
            DisposeRoslynState();
        }

        private static void DisposeRoslynState()
        {
            _scriptOptions = null;
            _scriptOptionsType = null;
            _csharpScriptType = null;
            _cachedEvaluateSourceTextMethod = null;
            _cachedEvaluateStringMethod = null;
            _cachedMetadataReferences = null;
            _roslynInitialized = false;
            _roslynInitError = null;
        }

        // ── Log capture ─────────────────────────────────────────────────────
        private static readonly List<string> CapturedLogs = new List<string>();

        // ================================================================== //
        //  Public API
        // ================================================================== //

        public static ExecutionResult Execute(
            string scriptCode,
            string scriptPath = null,
            int timeoutMs = 30_000)
        {
            CapturedLogs.Clear();
            var startTime = DateTime.Now;
            Debug.Log($"Executing script ...");

            try
            {
                scriptCode = scriptCode?.Trim() ?? "";
                // Strip BOM and zero-width characters that confuse Roslyn
                scriptCode = scriptCode.TrimStart('\uFEFF', '\u200B', '\u0000');

                object returnValue = null;
                Exception executionException = null;

                Application.logMessageReceived += CaptureLog;
                try
                {
                    returnValue = ExecuteScriptWithRoslyn(scriptCode, scriptPath, timeoutMs);
                }
                catch (Exception ex)
                {
                    executionException = ex;
                }
                finally
                {
                    Application.logMessageReceived -= CaptureLog;
                }

                if (executionException != null)
                {
                    return new ExecutionResult
                    {
                        Success = false,
                        Error = $"Execution error: {executionException.Message}\n{executionException.StackTrace}",
                        Logs = new List<string>(CapturedLogs),
                        ExecutionTimeMs = (DateTime.Now - startTime).TotalMilliseconds,
                    };
                }

                return new ExecutionResult
                {
                    Success = true,
                    ReturnValue = returnValue,
                    Logs = new List<string>(CapturedLogs),
                    ExecutionTimeMs = (DateTime.Now - startTime).TotalMilliseconds,
                };
            }
            catch (Exception ex)
            {
                return new ExecutionResult
                {
                    Success = false,
                    Error = $"Unexpected error: {ex.Message}\n{ex.StackTrace}",
                    Logs = new List<string>(CapturedLogs),
                    ExecutionTimeMs = (DateTime.Now - startTime).TotalMilliseconds,
                };
            }
        }

        // ================================================================== //
        //  Roslyn initialization
        // ================================================================== //

        private static void InitializeRoslyn()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_csharpScriptType == null)
                        _csharpScriptType = asm.GetType(
                            "Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript");
                    if (_scriptOptionsType == null)
                        _scriptOptionsType = asm.GetType(
                            "Microsoft.CodeAnalysis.Scripting.ScriptOptions");
                    if (_csharpScriptType != null && _scriptOptionsType != null) break;
                }

                if (_csharpScriptType == null || _scriptOptionsType == null)
                {
                    _roslynInitError =
                        "Microsoft.CodeAnalysis.CSharp.Scripting assembly not loaded. " +
                        "Add Microsoft.CodeAnalysis.CSharp.Scripting.dll and " +
                        "Microsoft.CodeAnalysis.Scripting.dll to Editor/Wuc/Plugins/.";
                    return;
                }

                // ScriptOptions.Default
                var opts = _scriptOptionsType
                    .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);

                // Use in-memory metadata references to avoid file locks during Unity recompilation.
                var addRefs = ResolveAddReferencesMethod();
                if (addRefs != null)
                {
                    _cachedMetadataReferences ??= BuildInMemoryMetadataReferences();
                    if (_cachedMetadataReferences.Length > 0)
                    {
                        try { opts = addRefs.Invoke(opts, new object[] { _cachedMetadataReferences }); } catch { }
                    }
                }

                // Add default using namespaces
                var addImports = _scriptOptionsType.GetMethod(
                    "AddImports", new[] { typeof(string[]) });
                if (addImports != null)
                {
                    var imports = new[]
                    {
                        "System",
                        "System.Linq",
                        "System.Collections.Generic",
                        "UnityEngine",
                        "UnityEditor",
                    };
                    try { opts = addImports.Invoke(opts, new object[] { imports }); } catch { }
                }

                _scriptOptions = opts;
                _roslynInitialized = true;
            }
            catch (Exception ex)
            {
                _roslynInitError = ex.Message;
            }
        }

        // ================================================================== //
        //  Script execution
        // ================================================================== //

        private static object ExecuteScriptWithRoslyn(string code, string scriptPath, int timeoutMs)
        {
            if (!_roslynInitialized)
                InitializeRoslyn();

            if (_roslynInitError != null)
                throw new InvalidOperationException($"Roslyn not available: {_roslynInitError}");

            var globals = new ScriptGlobals();

            // Stable virtual file path for diagnostics and stack traces
            var normalizedPath = NormalizeScriptPath(scriptPath);
            var options = InvokeMethod(_scriptOptions, "WithFilePath", normalizedPath);

            var task = EvaluateAsyncBestEffort(code, options, globals, typeof(ScriptGlobals));

            // WhenAny propagates the original script exception instead of wrapping it in AggregateException
            var completed = Task.WhenAny(task, Task.Delay(timeoutMs)).GetAwaiter().GetResult();
            if (!ReferenceEquals(completed, task))
                throw new TimeoutException($"Script execution timed out after {timeoutMs}ms");

            return task.GetAwaiter().GetResult();
        }

        private static Task<object> EvaluateAsyncBestEffort(
            string code,
            object baseOptions,
            ScriptGlobals globals,
            Type globalsType)
        {
            // Prefer SourceText overload (enables debug info); fall back to string overload (avoids CS8055)
            if (TryGetEvaluateAsyncSourceTextOverload(out var mi))
            {
                try
                {
                    var debugOptions = InvokeMethod(baseOptions, "WithEmitDebugInformation", true);
                    debugOptions = InvokeMethod(debugOptions, "WithOptimizationLevel",
                                           OptimizationLevel.Debug);

                    var sourceText = SourceText.From(code, Encoding.UTF8);
                    var ct = CancellationToken.None;

                    var result = mi.Invoke(
                        null,
                        new object[] { sourceText, debugOptions, globals, globalsType, ct });

                    return (Task<object>)result;
                }
                catch { /* fall through to string overload */ }
            }

            return EvaluateAsyncWithString(code, baseOptions, globals, globalsType);
        }

        private static Task<object> EvaluateAsyncWithString(
            string code,
            object options,
            ScriptGlobals globals,
            Type globalsType)
        {
            if (_cachedEvaluateStringMethod == null)
            {
                if (_csharpScriptType == null)
                    throw new InvalidOperationException("CSharpScript type not found.");

                foreach (var m in _csharpScriptType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "EvaluateAsync" && m.IsGenericMethodDefinition))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 5) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    if (_scriptOptionsType != null && ps[1].ParameterType != _scriptOptionsType) continue;
                    if (ps[2].ParameterType != typeof(object)) continue;
                    if (ps[3].ParameterType != typeof(Type)) continue;
                    if (ps[4].ParameterType != typeof(CancellationToken)) continue;

                    _cachedEvaluateStringMethod = m.MakeGenericMethod(typeof(object));
                    break;
                }

                if (_cachedEvaluateStringMethod == null)
                    throw new NotSupportedException(
                        "CSharpScript.EvaluateAsync(string,...) overload not found.");
            }

            var noDebugOptions = InvokeMethod(options, "WithEmitDebugInformation", false);
            object result;
            try
            {
                result = _cachedEvaluateStringMethod.Invoke(
                    null,
                    new object[] { code, noDebugOptions, globals, globalsType, CancellationToken.None });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }

            return (Task<object>)result;
        }

        // ================================================================== //
        //  TryGetEvaluateAsyncSourceTextOverload
        // ================================================================== //

        private static bool TryGetEvaluateAsyncSourceTextOverload(out MethodInfo method)
        {
            if (_cachedEvaluateSourceTextMethod != null)
            {
                method = _cachedEvaluateSourceTextMethod;
                return true;
            }

            method = null;
            try
            {
                if (_csharpScriptType == null) return false;

                // Expected signature:
                // EvaluateAsync<T>(SourceText code, ScriptOptions options,
                //                  object globals, Type globalsType, CancellationToken)
                var methods = _csharpScriptType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "EvaluateAsync" && m.IsGenericMethodDefinition)
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 5) continue;
                    if (ps[0].ParameterType != typeof(SourceText)) continue;
                    if (_scriptOptionsType != null && ps[1].ParameterType != _scriptOptionsType) continue;
                    if (ps[2].ParameterType != typeof(object)) continue;
                    if (ps[3].ParameterType != typeof(Type)) continue;
                    if (ps[4].ParameterType != typeof(CancellationToken)) continue;

                    method = m.MakeGenericMethod(typeof(object));
                    _cachedEvaluateSourceTextMethod = method;
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        // ================================================================== //
        //  Helpers
        // ================================================================== //

        private static MethodInfo ResolveAddReferencesMethod()
        {
            if (_scriptOptionsType == null) return null;

            foreach (var m in _scriptOptionsType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "AddReferences"))
            {
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;

                if (ps[0].ParameterType == typeof(MetadataReference[]))
                    return m;
            }

            foreach (var m in _scriptOptionsType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "AddReferences"))
            {
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;

                if (typeof(IEnumerable<MetadataReference>).IsAssignableFrom(ps[0].ParameterType))
                    return m;
            }

            return null;
        }

        private static MetadataReference[] BuildInMemoryMetadataReferences()
        {
            var references = new List<MetadataReference>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenAssemblyIdentities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null || asm.IsDynamic) continue;

                string identity;
                try { identity = asm.GetName().FullName; }
                catch { continue; }

                if (string.IsNullOrEmpty(identity)) continue;
                if (!seenAssemblyIdentities.Add(identity)) continue;

                string path;
                try { path = asm.Location; }
                catch { continue; }

                if (string.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path)) continue;
                if (!seenPaths.Add(path)) continue;

                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length == 0) continue;

                    references.Add(MetadataReference.CreateFromImage(ImmutableArray.Create(bytes)));
                }
                catch
                {
                    // Ignore a single assembly reference failure and keep building remaining refs.
                }
            }

            return references.ToArray();
        }

        // Invoke a ScriptOptions With* chain method via reflection
        private static object InvokeMethod(object target, string methodName, params object[] args)
        {
            if (target == null) return null;
            var argTypes = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
            var m = target.GetType().GetMethod(methodName, argTypes);
            return m != null ? m.Invoke(target, args) : target;
        }

        private static string NormalizeScriptPath(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                return "<script>";
            return scriptPath.Replace('\\', '/');
        }

        private static void CaptureLog(string condition, string stackTrace, LogType type)
        {
            var prefix = type == LogType.Error ? "[Error] "
                       : type == LogType.Warning ? "[Warning] "
                       : type == LogType.Assert ? "[Assert] "
                       : "";
            CapturedLogs.Add($"{prefix}{condition}");
        }

        private static void AppendLogEntry(LogEntry entry)
        {
            try
            {
                var persisted = new PersistedLogEntry
                {
                    timestamp = entry.Timestamp.ToString("O"),
                    type = entry.Type.ToString(),
                    message = entry.Message,
                    stackTrace = entry.StackTrace,
                };
                var line = JsonSerializer.Serialize(persisted);

                lock (_logFileLock)
                {
                    EnsureLogFileDirectory();
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Utf8WithoutBom);
                }
            }
            catch
            {
                // Avoid recursive logging if file persistence fails.
            }
        }

        private static bool TryParsePersistedLogEntry(string line, out LogEntry entry)
        {
            entry = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedLogEntry>(line);
                if (persisted == null)
                    return false;

                if (!DateTime.TryParse(persisted.timestamp, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                    return false;

                if (!Enum.TryParse(persisted.type, out LogType type))
                    type = LogType.Log;

                entry = new LogEntry
                {
                    Timestamp = timestamp,
                    Type = type,
                    Message = persisted.message ?? string.Empty,
                    StackTrace = persisted.stackTrace ?? string.Empty,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureLogFileDirectory()
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        private static string GetLogFilePath()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "Temp", "wuc.log");
        }

        private class PersistedLogEntry
        {
            public string timestamp { get; set; }
            public string type { get; set; }
            public string message { get; set; }
            public string stackTrace { get; set; }
        }
    }
}
