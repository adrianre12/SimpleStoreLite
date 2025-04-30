using VRage.Utils;

namespace SimpleStoreLite.StoreBlock
{
    public static class MyLogExtension
    {
        public static void WriteLineIf(this MyLog myLog, bool enable, string message)
        {
            if (enable)
                myLog.WriteLine($"[DEBUG] {message}");
        }
    }
}
