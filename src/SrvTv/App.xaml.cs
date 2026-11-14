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
            var nativePath = FindLibVlcPath();
            LibVLCSharp.Shared.Core.Initialize(nativePath);
            Log.Write("LibVLC core initialized from " + nativePath);
        }
        catch (Exception ex)
        {
            Log.Write("LibVLC init FAILED: " + ex);
            AppState.Instance.SetLibVlcError(
                "Video engine unavailable. Reinstall the Windows package or check the libvlc files."
            );
        }
        // Dark title bars on every window so the frame matches the app.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) UseDarkTitleBar(w); }));
        base.OnStartup(e);
        Updater.StartBackgroundChecks();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void UseDarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = 0x001D1512; // COLORREF (BGR) for #12151D, the header colour
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Win11)
        }
        catch { /* older Windows: keep the default frame */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Instance.Shutdown(); } catch { }
        // A downloaded update installs quietly once the app has closed.
        if (Updater.PendingSetup != null) Updater.Apply(relaunch: false);
        Log.Write("=== exit ===");
        base.OnExit(e);
    }
    private static string FindLibVlcPath()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            System.Runtime.InteropServices.Architecture.X86 => "win-x86",
            _ => "win-x64",
        };
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libvlc", arch),
            Path.Combine(AppContext.BaseDirectory, "libvlc"),
            AppContext.BaseDirectory,
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "libvlc.dll")) &&
                File.Exists(Path.Combine(candidate, "libvlccore.dll")))
                return candidate;
        }
        throw new FileNotFoundException(
            "libvlc.dll/libvlccore.dll were not found under the application directory. " +
            "Expected publish\\libvlc\\" + arch
        );
    }
}
