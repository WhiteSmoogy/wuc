namespace Wuc
{
    internal static class WucDaemonCommandRouter
    {
        internal static string DispatchToJson(string path, string body)
        {
            return WucServer.DispatchCommandToJson(path, body);
        }
    }
}
