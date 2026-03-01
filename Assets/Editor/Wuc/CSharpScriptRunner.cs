using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
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
        internal StringBuilder Output;

        public void print(object value) => Output?.AppendLine(value?.ToString() ?? "null");
        public void log(object value) => Debug.Log(value);
    }

    public class ExecutionResult
    {
        public bool Success { get; set; }
        public object ReturnValue { get; set; }
        public string Error { get; set; }
        public string Output { get; set; }
        public List<string> Logs { get; set; }
        public double ExecutionTimeMs { get; set; }
    }

    [InitializeOnLoad]
    public static class CSharpScriptRunner
    {
        // ── Persistent log buffer ──────────────────────────────────────────
        private const int MaxLogBufferSize = 500;

        private static readonly Queue<LogEntry> _logBuffer = new Queue<LogEntry>();
        private static readonly object _logLock = new object();
        private static readonly object _roslynLock = new object();

        static CSharpScriptRunner()
        {
            Application.logMessageReceived += OnPersistentLogReceived;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
        }

        /// <summary>Returns the most recent <paramref name="count"/> Unity log entries.</summary>
        public static List<LogEntry> GetRecentLogs(int count = 100)
        {
            lock (_logLock)
            {
                var arr = _logBuffer.ToArray();
                int skip = Math.Max(0, arr.Length - count);
                return arr.Skip(skip).ToList();
            }
        }

        /// <summary>Clears the persistent log buffer.</summary>
        public static void ClearLogBuffer()
        {
            lock (_logLock) { _logBuffer.Clear(); }
        }

        private static void OnPersistentLogReceived(string condition, string stackTrace, LogType type)
        {
            lock (_logLock)
            {
                if (_logBuffer.Count >= MaxLogBufferSize)
                    _logBuffer.Dequeue();

                _logBuffer.Enqueue(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Type = type,
                    Message = condition,
                    StackTrace = stackTrace,
                });
            }
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
            Application.logMessageReceived -= OnPersistentLogReceived;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            DisposeRoslynState();
        }

        private static void OnCompilationStarted(object context)
        {
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

        // ── Output / log capture ───────────────────────────────────────────
        private static readonly StringBuilder OutputBuffer = new StringBuilder();
        private static readonly List<string> CapturedLogs = new List<string>();

        // ================================================================== //
        //  Public API
        // ================================================================== //

        public static ExecutionResult Execute(
            string scriptCode,
            string scriptPath = null,
            int timeoutMs = 30_000)
        {
            OutputBuffer.Clear();
            CapturedLogs.Clear();
            var startTime = DateTime.Now;

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
                        Output = OutputBuffer.ToString(),
                        Logs = new List<string>(CapturedLogs),
                        ExecutionTimeMs = (DateTime.Now - startTime).TotalMilliseconds,
                    };
                }

                return new ExecutionResult
                {
                    Success = true,
                    ReturnValue = returnValue,
                    Output = OutputBuffer.ToString(),
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
                    Output = OutputBuffer.ToString(),
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

            var globals = new ScriptGlobals { Output = OutputBuffer };

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
    }
}
