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
        private static extern void wuc_daemon_shutdown();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_set_state(int state);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_state();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_queue_depth();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wuc_daemon_enqueue(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestClass);

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wuc_daemon_dequeue_json();

        [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wuc_daemon_string_free(IntPtr ptr);

        internal static bool TryInit(int maxQueueSize)
        {
            try { return wuc_daemon_init(maxQueueSize) == 1; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
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

        // 1=ok, 0=full/error
        internal static bool Enqueue(string path, string body, string requestId, string requestClass)
        {
            try
            {
                return wuc_daemon_enqueue(path ?? string.Empty, body ?? string.Empty, requestId ?? string.Empty, requestClass ?? string.Empty) == 1;
            }
            catch
            {
                return false;
            }
        }

        internal static string DequeueJson()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = wuc_daemon_dequeue_json();
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
