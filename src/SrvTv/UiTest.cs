using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SrvTv;

// Dev aid, dormant unless the environment variable SRVTV_UITEST=1 is set.
// Windows then open off-screen and never keep focus, playback is muted,
// and a script can drive each window through a command file, so the UI
// can be exercised and captured while the real app is in use on the same
// PC. Command file: %TEMP%\srvtv-uitest.txt, one "<seq> <window> <cmd>
// [arg]" per line (cmd: key/keydown/keyup <Key>, or a window's own verbs).
internal static class UiTest
{
    public static readonly bool On =
        Environment.GetEnvironmentVariable("SRVTV_UITEST") == "1";

    // Which part of the main window "has" keyboard focus. Real focus needs
    // an active window, which test windows never are.
    public static string Zone = "grid";

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private static string CommandFile => Path.Combine(Path.GetTempPath(), "srvtv-uitest.txt");
    private static bool _guarding;
    private static IntPtr _userForeground;

    public static void Place(Window w, double width = 1536, double height = 864)
    {
        if (!On) return;
        GuardForeground();
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.WindowState = WindowState.Normal;
        w.Left = -20000;
        w.Top = 0;
        w.Width = width;
        w.Height = height;
        w.SourceInitialized += (_, _) => NoActivate(w);
    }

    public static void NoActivate(Window w)
    {
        if (!On) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExNoActivate | WsExToolWindow));
    }

    // Some code paths (the video view's overlay window, Focus() calls) still
    // activate a test window. Hand the foreground straight back to whatever
    // the user was in.
    private static void GuardForeground()
    {
        if (_guarding) return;
        _guarding = true;
        var me = (uint)Environment.ProcessId;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            GetWindowThreadProcessId(fg, out var pid);
            if (pid != me) _userForeground = fg;
            else if (_userForeground != IntPtr.Zero) SetForegroundWindow(_userForeground);
        };
        timer.Start();
    }

    // Keyboard-focus test that also works for never-activated test windows.
    public static bool FocusWithin(UIElement element, string zone) =>
        On ? Zone == zone : element.IsKeyboardFocusWithin;

    // Poll the command file for lines addressed to this window.
    public static void Attach(Window w, string name, Action<string, string>? verbs = null)
    {
        if (!On) return;
        // Only act on commands written after this window opened.
        var last = 0L;
        try
        {
            if (File.Exists(CommandFile))
                foreach (var line in File.ReadAllLines(CommandFile))
                    if (long.TryParse(line.Split(' ')[0], out var s) && s > last) last = s;
        }
        catch { }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            string[] lines;
            try { lines = File.Exists(CommandFile) ? File.ReadAllLines(CommandFile) : Array.Empty<string>(); }
            catch { return; }
            foreach (var line in lines)
            {
                var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !long.TryParse(parts[0], out var seq) || seq <= last) continue;
                if (parts[1] != name) continue;
                last = seq;
                var arg = parts.Length > 3 ? parts[3] : "";
                Log.Write($"uitest {name}: {parts[2]} {arg}");
                try
                {
                    switch (parts[2])
                    {
                        case "key": Raise(w, arg, down: true); Raise(w, arg, down: false); break;
                        case "keydown": Raise(w, arg, down: true); break;
                        case "keyup": Raise(w, arg, down: false); break;
                        case "snap": SnapAll(arg); break;
                        default: verbs?.Invoke(parts[2], arg); break;
                    }
                }
                catch (Exception ex) { Log.Write("uitest command failed: " + line + " " + ex.Message); }
            }
        };
        timer.Start();
        w.Closed += (_, _) => timer.Stop();
    }

    // Render every open window (including the video view's overlay window,
    // which screen capture can't see) to %TEMP%\srvtv-snap-<prefix>-<n>.png.
    private static void SnapAll(string prefix)
    {
        var n = 0;
        foreach (Window win in Application.Current.Windows)
        {
            if (win.Content is not FrameworkElement root || root.ActualWidth < 1) continue;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Media.DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(System.Windows.Media.Brushes.DimGray, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var name = new string((win.Title.Length > 0 ? win.Title : win.GetType().Name)
                .Where(char.IsLetterOrDigit).ToArray());
            using var fs = File.Create(Path.Combine(Path.GetTempPath(), $"srvtv-snap-{prefix}-{n++}-{name}.png"));
            enc.Save(fs);
        }
    }

    private static void Raise(Window w, string keyName, bool down)
    {
        var source = PresentationSource.FromVisual(w);
        if (source == null || !Enum.TryParse<Key>(keyName, true, out var key)) return;
        w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent,
        });
    }
}
