using System.Windows;

namespace SrvTv;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("FATAL dispatcher: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("FATAL domain: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("FATAL task: " + args.Exception);
            args.SetObserved();
        };
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            Log.Write("LibVLC core initialized");
        }
        catch (Exception ex)
        {
            Log.Write("LibVLC init FAILED: " + ex);
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Instance.Shutdown(); } catch { }
        Log.Write("=== exit ===");
        base.OnExit(e);
    }
}
