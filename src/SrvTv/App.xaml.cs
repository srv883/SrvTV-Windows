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
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Instance.Shutdown(); } catch { }
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
