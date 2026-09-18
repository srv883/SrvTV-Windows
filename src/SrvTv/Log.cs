namespace SrvTv;

// Tiny file logger (%LocalAppData%\SrvTv\app.log). Used for smoke-test
// diagnostics before any UI log surface exists.
public static class Log
{
    private static readonly object Gate = new();
    private static string _path = "";

    public static void Init()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "app.log");
            Write("=== Srv TV Windows start " + DateTime.Now.ToString("s") + " ===");
        }
        catch { }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (_path.Length == 0) return;
                File.AppendAllText(_path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}
