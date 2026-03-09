using System;
using System.Runtime.InteropServices;

namespace Wuc
{
    internal static class WucNativeDaemonBridge
    {
        private const string NativeLib = "wuc_daemon_runtime";

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_init(int maxQueueSize);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_attach_managed(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string projectId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string projectPath,
            int pid,
            int portRangeStart,
            int portRangeEnd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string startedAtUtc,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string updatedAtUtc);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_shutdown();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_set_state(int state);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_state();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_queue_depth();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wuc_daemon_identity_json();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wuc_daemon_try_dequeue_command_json();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_complete_command(
            ulong commandId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string responseJson);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_record_log(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string timestampUtc,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string timestamp,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string stackTrace);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_string_free(IntPtr ptr);

        internal static bool TryInit(int maxQueueSize)
        {
            try { return wuc_daemon_init(maxQueueSize) == 1; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }

        internal static int AttachManaged(
            string projectId,
            string projectPath,
            int pid,
            int portRangeStart,
            int portRangeEnd,
            string startedAtUtc,
            string updatedAtUtc)
        {
            try
            {
                return wuc_daemon_attach_managed(
                    projectId ?? string.Empty,
                    projectPath ?? string.Empty,
                    pid,
                    portRangeStart,
                    portRangeEnd,
                    startedAtUtc ?? string.Empty,
                    updatedAtUtc ?? string.Empty);
            }
            catch
            {
                return 0;
            }
        }

        internal static void Shutdown()
        {
            try { wuc_daemon_shutdown(); } catch { }
        }

        internal static void SetState(WucDaemonState state)
        {
            try { wuc_daemon_set_state(state == WucDaemonState.Ready ? 0 : 1); } catch { }
        }

        internal static WucDaemonState CurrentState()
        {
            try { return wuc_daemon_state() == 0 ? WucDaemonState.Ready : WucDaemonState.Reloading; }
            catch { return WucDaemonState.Reloading; }
        }

        internal static int QueueDepth()
        {
            try { return wuc_daemon_queue_depth(); }
            catch { return 0; }
        }

        internal static string IdentityJson()
        {
            return ReadNativeString(() => wuc_daemon_identity_json());
        }

        internal static string DequeueCommandJson()
        {
            return ReadNativeString(() => wuc_daemon_try_dequeue_command_json());
        }

        internal static bool CompleteCommand(ulong commandId, string responseJson)
        {
            try
            {
                return wuc_daemon_complete_command(commandId, responseJson ?? "{}") == 1;
            }
            catch
            {
                return false;
            }
        }

        internal static void RecordLog(LogEntry entry)
        {
            try
            {
                wuc_daemon_record_log(
                    entry.Timestamp.ToUniversalTime().ToString("O"),
                    entry.Timestamp.ToString("HH:mm:ss.fff"),
                    entry.Type.ToString(),
                    entry.Message ?? string.Empty,
                    entry.StackTrace ?? string.Empty);
            }
            catch { }
        }

        private static string ReadNativeString(Func<IntPtr> thunk)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = thunk();
                if (ptr == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    wuc_daemon_string_free(ptr);
            }
        }
    }
}
