using LibVLCSharp.Shared;
using SrvTv.Models;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SrvTv;

public partial class PlayerWindow : Window
{
    private Channel _channel;
    private string? _catId;
    private string? _catName;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _parked;
    private bool _exitOpen;
    private bool _escHoldFired;
    private readonly DispatcherTimer _escTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly string[] _aspects = { "", "16:9", "4:3" };
    private int _aspectIdx;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<Channel> _failurePlan = new();
    private int _failurePlanPos;
    private string? _failurePlanId;
    private DateTime _lastPlaybackErrorUtc = DateTime.MinValue;
    private int _sameSourceRetries;
    private bool _closing;

    // Channel guide (TV parity): frozen per-section order, double-Enter
    // opens the full schedule, Esc closes.
    private bool _guideOpen;
    private readonly Dictionary<string, List<Channel>> _frozenSections = new();
    private string? _lastOkId;
    private long _lastOkMs;
    private const long GuideDoubleMs = 450;

    public sealed class GuideRow
    {
        public string Number { get; set; } = "";
        public string Name { get; set; } = "";
        public string Logo { get; set; } = "";
        public string Epg { get; set; } = "";
        public bool HasEpg { get; set; }
        public string Meta { get; set; } = "";
        public bool IsHd { get; set; }
    }

    public PlayerWindow(Channel channel, string? catId, string? catName)
    {
        _channel = channel;
        _catId = catId;
        _catName = catName;
        InitializeComponent();
        UiTest.Place(this);
        UiTest.Attach(this, "player");
        Loaded += (_, _) => StartPlayback();
        Closed += (_, _) =>
        {
            Cleanup();
            // Show the mini player right away, even if the home window isn't re-activated.
            if (_parked) Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.SyncPip();
        };
        _escTimer.Tick += (_, _) =>
        {
            _escTimer.Stop();
            _escHoldFired = true;
            ShowExitOverlay();
        };
        _hideTimer.Tick += (_, _) => HideOsd();
        _windowed = AppState.Instance.Prefs.GetInt("player_windowed") == 1;
        SourceInitialized += (_, _) => ApplyWindowMode(save: false);
        _clockTimer.Tick += (_, _) => TickClock();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _recoveryTimer.Tick += (_, _) => RecoverPlayback();
    }

    private void StartPlayback()
    {
        try
        {
            var lib = AppState.Instance.EnsureLib();
            _player = new MediaPlayer(lib);
            _player.Buffering += OnBuffering;
            _player.Playing += OnPlaying;
            _player.EncounteredError += OnPlayerError;
            Video.MediaPlayer = _player;
        }
        catch (Exception ex)
        {
            Log.Write("player start FAILED: " + ex);
            ShowToast("Video engine unavailable");
            return;
        }
        PlayChannel(_channel);
    }

    private void PlayChannel(Channel channel)
    {
        if (_player == null) return;
        channel = AppState.Instance.Repo.GetChannelById(channel.Id) ?? channel;
        try { _player.Stop(); } catch { }
        try { _media?.Dispose(); } catch { }
        _media = null;
        _channel = channel;
        ChannelName.Text = channel.Name;
        ChannelMeta.Text = $"{LabelConverter.Tidy(channel.Category)}  •  {LabelConverter.Tidy(channel.Language)}";
        UpdateEpgLine();
        TickClock();
        _clockTimer.Start();
        ShowOsd();
        try
        {
            var lib = AppState.Instance.EnsureLib();
            _media = new Media(lib, channel.Url, FromType.FromLocation);
            if (IsSonyTen3(channel))
            {
                // The Ten 3 panel feed has brief segment/timestamp hiccups.
                // A deeper LibVLC network buffer absorbs those without
                // changing the source URL or affecting other channels.
                _media.AddOption(":network-caching=5000");
                _media.AddOption(":live-caching=5000");
            }
            if (UiTest.On) _media.AddOption(":no-audio");
            _player.Play(_media);
            Log.Write("playing: " + channel.Name);
        }
        catch (Exception ex)
        {
            Log.Write("play channel FAILED: " + ex);
            ShowToast("Could not start playback");
        }
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (_closing || _parked) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && !_parked)
                    Buffering.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed;
            });
        }
        catch { }
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (_closing || _parked) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing || _parked) return;
                _recoveryTimer.Stop();
                _failurePlan.Clear();
                _failurePlanPos = 0;
                _failurePlanId = null;
                Buffering.Visibility = Visibility.Collapsed;
                AppState.Instance.Repo.RecordWatch(_channel.Id);
            });
        }
        catch { }
    }

    private void OnPlayerError(object? sender, EventArgs e)
    {
        if (_closing || _parked) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing || _parked) return;
                var now = DateTime.UtcNow;
                if (IsSonyTen3(_channel) && now - _lastPlaybackErrorUtc < TimeSpan.FromSeconds(8))
                {
                    // LibVLC can emit multiple error callbacks for one brief
                    // live segment fault. Ignore the callback burst instead
                    // of stopping/reopening the stream repeatedly.
                    Log.Write("ignored duplicate Sony Ten 3 playback error");
                    return;
                }
                _lastPlaybackErrorUtc = now;
                Buffering.Visibility = Visibility.Collapsed;
                if (_failurePlanId != _channel.Id)
                {
                    _failurePlanId = _channel.Id;
                    _failurePlanPos = 0;
                    _sameSourceRetries = 0;
                    _failurePlan.Clear();
                    _failurePlan.Add(_channel);
                    _failurePlan.AddRange(AppState.Instance.Repo.FindAlternates(_channel).Take(3));
                }
                if (IsSonyTen3(_channel) && _sameSourceRetries >= 2 && _failurePlanPos == 0)
                    _failurePlanPos = 1; // after two fresh opens, use a known mirror if available
                ShowToast(_failurePlanPos == 0 ? "Signal glitch — reconnecting..." : "Trying alternate source...");
                Log.Write("playback error: " + _channel.Url);
                _recoveryTimer.Stop();
                _recoveryTimer.Start();
            });
        }
        catch { }
    }

    private void RecoverPlayback()
    {
        _recoveryTimer.Stop();
        if (_closing || _parked || _player == null) return;
        if (_failurePlanPos < _failurePlan.Count)
        {
            var next = _failurePlan[_failurePlanPos++];
            if (IsSonyTen3(_channel) && next.Url == _channel.Url) _sameSourceRetries++;
            PlayChannel(next);
            return;
        }
        _failurePlan.Clear();
        _failurePlanPos = 0;
        _failurePlanId = null;
        ShowToast("Channel not working — moving on");
        SwitchChannel(1);
    }

    private static bool IsSonyTen3(Channel channel)
    {
        var name = channel.Name.ToLowerInvariant();
        return name.Contains("ten 3") || name.Contains("ten3") || name == "ten 3";
    }

    private void UpdateEpgLine()
    {
        try
        {
            var (now, next) = AppState.Instance.Epg.NowNext(_channel.Id);
            var sb = new System.Text.StringBuilder();
            if (now != null) sb.Append("▶ ").Append(now.Title);
            if (now != null && next != null) sb.Append("   •   ");
            if (next != null) sb.Append("Next: ").Append(next.Title);
            EpgNowNext.Text = sb.ToString();
        }
        catch { EpgNowNext.Text = ""; }
    }

    private void TickClock()
    {
        Clock.Text = _player is { IsPlaying: true } && _player.Time > 0 && _player.Length <= 0
            ? "LIVE • " + Fmt(_player.Time)
            : _player != null ? Fmt(_player.Time) : "";
        UpdateEpgLine();
    }

    private static string Fmt(long ms)
    {
        var s = ms / 1000;
        return s >= 3600 ? $"{s / 3600:00}:{(s % 3600) / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    private void ShowOsd()
    {
        InfoBar.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideOsd()
    {
        _hideTimer.Stop();
        // Keep the bar up while the pointer is on it (its buttons are in use).
        if (InfoBar.IsMouseOver) { _hideTimer.Start(); return; }
        InfoBar.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Collapsed;
        if (!_guideOpen && !_exitOpen) Overlay.Cursor = Cursors.None; // hide the pointer over video
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ShowOsd();
        if (_exitOpen)
        {
            switch (e.Key)
            {
                case Key.Escape: HideExitOverlay(); break;
                case Key.Left: case Key.Right: case Key.Tab: case Key.Up: case Key.Down:
                    SelectExitChoice(!_exitYes); break;
                case Key.Enter: case Key.Space:
                    if (_exitYes) OnExitYes(this, new RoutedEventArgs()); else HideExitOverlay();
                    break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11 || (e.Key == Key.W && !_guideOpen))
        {
            ToggleWindowMode();
            e.Handled = true;
            return;
        }
        if (_guideOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.G:
                    CloseGuide();
                    e.Handled = true;
                    return;
                case Key.Left:
                    SwitchGuideSection(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    SwitchGuideSection(1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    // Rows consume DOWN themselves, so detect the double
                    // here: second press on the same row opens its full
                    // schedule (row never sees DOWN, so no replay).
                    if (!e.IsRepeat)
                    {
                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var fid = FocusedGuideId();
                        if (fid != null && fid == _lastOkId && nowMs - _lastOkMs <= GuideDoubleMs)
                        {
                            _lastOkId = null;
                            _lastOkMs = 0;
                            var ch = ScopedChannels().FirstOrDefault(c => c.Id == fid);
                            if (ch != null)
                            {
                                new ScheduleWindow(ch.Id, ch.Name).Show();
                                e.Handled = true;
                                return;
                            }
                        }
                        _lastOkId = fid;
                        _lastOkMs = nowMs;
                    }
                    // Single Enter watches the focused row (same channel =
                    // no-op, guide stays for a possible double).
                    if (!e.IsRepeat)
                    {
                        var wid = FocusedGuideId();
                        var wch = wid == null ? null
                            : ScopedChannels().FirstOrDefault(c => c.Id == wid);
                        if (wch != null && wch.Id != _channel.Id)
                        {
                            PlayChannel(wch);
                            CloseGuide();
                            e.Handled = true;
                            return;
                        }
                    }
                    return; // same-row: let nothing happen (guide stays open)
                case Key.Up: StepGuide(-1); e.Handled = true; return;
                case Key.Down: StepGuide(1); e.Handled = true; return;
                case Key.PageUp: StepGuide(-8); e.Handled = true; return;
                case Key.PageDown: StepGuide(8); e.Handled = true; return;
                case Key.Home: StepGuide(-100000); e.Handled = true; return;
                case Key.End: StepGuide(100000); e.Handled = true; return;
                default:
                    return; // ignore everything else behind the guide
            }
        }
        switch (e.Key)
        {
            case Key.Escape:
                if (!e.IsRepeat)
                {
                    _escHoldFired = false;
                    _escTimer.Start();
                }
                e.Handled = true;
                return;
            case Key.F:
                ToggleFavorite();
                e.Handled = true;
                return;
            case Key.A:
                CycleAspect();
                e.Handled = true;
                return;
            case Key.G:
                OpenGuide();
                e.Handled = true;
                return;
            case Key.Up:
                SwitchChannel(1); // DTH: Up = next channel
                e.Handled = true;
                return;
            case Key.Down:
                SwitchChannel(-1); // DTH: Down = previous channel
                e.Handled = true;
                return;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _escTimer.Stop();
        e.Handled = true;
        if (_escHoldFired) { _escHoldFired = false; return; }
        ParkAndClose(); // tap = back to grid, keeps playing in PiP
    }

    private void ToggleFavorite()
    {
        var repo = AppState.Instance.Repo;
        repo.ToggleFavorite(_channel.Id);
        repo.RefreshFavorites();
        ShowToast(repo.IsFavorite(_channel.Id)
            ? $"{_channel.Name} added to Favorites ★"
            : $"{_channel.Name} removed from Favorites");
    }

    private void CycleAspect()
    {
        if (_player == null) return;
        _aspectIdx = (_aspectIdx + 1) % _aspects.Length;
        try
        {
            _player.AspectRatio = _aspects[_aspectIdx].Length == 0 ? null : _aspects[_aspectIdx];
            ShowToast("Aspect: " + (_aspects[_aspectIdx].Length == 0 ? "Fit" : _aspects[_aspectIdx]));
        }
        catch { }
    }

    private void ParkAndClose()
    {
        try
        {
            // Exactly one parked player: drop any previous park first
            // (otherwise two streams sound at once).
            AppState.Instance.ReleaseParked();
            AppState.Instance.ParkedPlayer = _player;
            AppState.Instance.ParkedChannel = _channel;
            AppState.Instance.ParkedCategoryId = _catId;
            AppState.Instance.ParkedCategoryName = _catName;
            DetachPlayerHandlers();
            _player = null;
            _parked = true;
        }
        catch (Exception ex) { Log.Write("park failed: " + ex.Message); }
        Close();
    }

    // ----- channel guide (drawer) -----

    private List<Channel> ScopedChannels()
    {
        var key = _catId ?? "all";
        if (_frozenSections.TryGetValue(key, out var frozen)) return frozen;
        var repo = AppState.Instance.Repo;
        var list = _catId == null
            ? (repo.Channels.Count > 0 ? repo.Channels : new List<Channel> { _channel })
            : repo.GetChannelsForCategory(_catId, _catName ?? "");
        if (list.Count == 0) list = new List<Channel> { _channel };
        _frozenSections[key] = list;
        return list;
    }

    private void OpenGuide()
    {
        if (_guideOpen) return;
        _guideOpen = true;
        _lastOkId = null;
        _lastOkMs = 0;
        InfoBar.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Collapsed;
        GuidePanel.Visibility = Visibility.Visible;
        RefreshGuideList();
    }

    private void CloseGuide()
    {
        if (!_guideOpen) return;
        _guideOpen = false;
        _lastOkId = null;
        _lastOkMs = 0;
        GuidePanel.Visibility = Visibility.Collapsed;
        ShowOsd();
    }

    private void RefreshGuideList()
    {
        var channels = ScopedChannels();
        var rows = new List<GuideRow>();
        foreach (var ch in channels)
        {
            string? now = null, next = null;
            try
            {
                var (n, x) = AppState.Instance.Epg.NowNext(ch.Id);
                now = n?.Title;
                next = x == null ? null : $"{x.ClockLabel()}: {x.Title}";
            }
            catch { }
            var sb = new System.Text.StringBuilder();
            if (now != null) sb.Append("▶ ").Append(now);
            if (now != null && next != null) sb.AppendLine();
            if (next != null) sb.Append("Next: ").Append(next);
            var meta = LabelConverter.Tidy(ch.Category);
            if (ch.Language.Length > 0) meta += "  •  " + LabelConverter.Tidy(ch.Language);
            rows.Add(new GuideRow
            {
                Number = $"{Math.Max(0, ch.ChannelNumber):000}",
                Name = ch.Name,
                Logo = ch.Logo,
                Epg = sb.ToString(),
                HasEpg = sb.Length > 0,
                Meta = meta,
                IsHd = ch.IsHD,
            });
        }
        GuideList.ItemsSource = rows;
        GuideHeader.Text = LabelConverter.Tidy(_catName ?? "All Channels");
        var at = channels.FindIndex(c => c.Id == _channel.Id);
        GuideList.SelectedIndex = Math.Max(0, at);
        GuideList.ScrollIntoView(GuideList.SelectedItem);
        Dispatcher.BeginInvoke(() =>
        {
            if (GuideList.ItemContainerGenerator.ContainerFromIndex(GuideList.SelectedIndex)
                is System.Windows.Controls.ListBoxItem it) it.Focus();
        });
    }

    private string? FocusedGuideId()
    {
        try
        {
            var rows = GuideList.ItemsSource as List<GuideRow>;
            var channels = ScopedChannels();
            var idx = GuideList.SelectedIndex;
            if (channels.Count == 0 || idx < 0 || idx >= channels.Count) return null;
            return channels[idx].Id;
        }
        catch { return null; }
    }

    private void SwitchGuideSection(int delta)
    {
        var sections = AppState.Instance.Repo.Categories;
        if (sections.Count == 0) return;
        var cur = sections.FindIndex(c => c.Id == _catId);
        if (cur < 0) cur = 0;
        var target = Math.Clamp(cur + delta, 0, sections.Count - 1);
        if (target == cur) return;
        _catId = sections[target].Id;
        _catName = sections[target].Name;
        _lastOkId = null;
        _lastOkMs = 0;
        RefreshGuideList();
        ShowToast(sections[target].Name);
    }

    private void StepGuide(int delta)
    {
        var count = GuideList.Items.Count;
        if (count == 0) return;
        GuideList.SelectedIndex = Math.Clamp(GuideList.SelectedIndex + delta, 0, count - 1);
        GuideList.ScrollIntoView(GuideList.SelectedItem);
    }

    private void OnGuideDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var wid = FocusedGuideId();
        var wch = wid == null ? null : ScopedChannels().FirstOrDefault(c => c.Id == wid);
        if (wch == null) return;
        if (wch.Id != _channel.Id) PlayChannel(wch);
        CloseGuide();
    }

    private void SwitchChannel(int direction)    {
        var channels = ScopedChannels();
        if (channels.Count == 0) return;
        var cur = channels.FindIndex(c => c.Id == _channel.Id);
        var at = (cur < 0 ? 0 : cur + direction + channels.Count) % channels.Count;
        PlayChannel(channels[at]);
    }

    // ----- mouse over the picture, toolbar buttons -----

    private Point _lastMouse;

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !_guideOpen && !_exitOpen && ReferenceEquals(e.OriginalSource, Overlay))
        {
            ToggleWindowMode();
            e.Handled = true;
            return;
        }
        if (!_guideOpen) ShowOsd(); // a click on the picture re-reveals the info bar
        ReclaimKeyboard();
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) < 3) return;
        _lastMouse = p;
        if (!_guideOpen) ShowOsd();
    }

    // Clicking the overlay activates the overlay window; hand activation
    // back so the remote-style keys keep reaching this window.
    private void ReclaimKeyboard()
    {
        if (UiTest.On) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_closing) Activate(); }));
    }

    private void OnBtnGuide(object sender, RoutedEventArgs e) { OpenGuide(); ReclaimKeyboard(); }
    private void OnBtnMini(object sender, RoutedEventArgs e) => ParkAndClose();
    private void OnBtnWindowMode(object sender, RoutedEventArgs e) { ToggleWindowMode(); ReclaimKeyboard(); }
    private void OnBtnClose(object sender, RoutedEventArgs e) => Close();

    // ----- full screen / window -----

    private bool _windowed;

    private void ToggleWindowMode()
    {
        _windowed = !_windowed;
        ApplyWindowMode(save: true);
        ShowOsd();
    }

    private void ApplyWindowMode(bool save)
    {
        var prefs = AppState.Instance.Prefs;
        if (_windowed)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            var area = SystemParameters.WorkArea;
            double w = prefs.GetInt("player_win_w"), h = prefs.GetInt("player_win_h");
            double l = prefs.GetInt("player_win_l", int.MinValue), t = prefs.GetInt("player_win_t", int.MinValue);
            if (w < 480 || h < 270) { w = Math.Min(1280, area.Width * 0.7); h = Math.Round(w * 9 / 16); }
            w = Math.Min(w, area.Width);
            h = Math.Min(h, area.Height);
            var onScreen = l > SystemParameters.VirtualScreenLeft - w / 2
                           && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                           && t >= SystemParameters.VirtualScreenTop - 10
                           && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
            if (!onScreen) { l = area.Left + (area.Width - w) / 2; t = area.Top + (area.Height - h) / 2; }
            Width = w; Height = h;
            if (UiTest.On) { Left = -20000; Top = 0; } else { Left = l; Top = t; }
            MinWidth = 480; MinHeight = 300;
            BtnWindowMode.Content = "\uE740";
            BtnWindowMode.ToolTip = "Full screen (F11 or double-click)";
        }
        else
        {
            if (save) SaveWindowBounds(force: true);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // re-maximize so the frameless window covers the taskbar
            if (!UiTest.On) WindowState = WindowState.Maximized;
            BtnWindowMode.Content = "\uE73F";
            BtnWindowMode.ToolTip = "Play in a window (F11 or double-click)";
        }
        if (save) { prefs.PutInt("player_windowed", _windowed ? 1 : 0); prefs.Apply(); }
    }

    private void SaveWindowBounds(bool force = false)
    {
        if (UiTest.On || WindowState != WindowState.Normal || (!_windowed && !force)) return;
        if (WindowStyle == WindowStyle.None) return; // full screen bounds are not a window size
        var prefs = AppState.Instance.Prefs;
        prefs.PutInt("player_win_l", (int)Left);
        prefs.PutInt("player_win_t", (int)Top);
        prefs.PutInt("player_win_w", (int)Width);
        prefs.PutInt("player_win_h", (int)Height);
        prefs.Apply();
    }

    private bool _exitYes;

    private void ShowExitOverlay()
    {
        _exitOpen = true;
        ExitOverlay.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        SelectExitChoice(false);
    }

    // The dialog lives on the video overlay window, which never holds
    // keyboard focus, so the highlighted choice is tracked here.
    private void SelectExitChoice(bool yes)
    {
        _exitYes = yes;
        BtnExitYes.Style = (Style)FindResource(yes ? "BtnPrimary" : "Btn");
        BtnExitNo.Style = (Style)FindResource(yes ? "Btn" : "BtnPrimary");
    }

    private void HideExitOverlay()
    {
        _exitOpen = false;
        ExitOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnExitNo(object sender, RoutedEventArgs e) => HideExitOverlay();

    private void OnExitYes(object sender, RoutedEventArgs e)
    {
        AppState.Instance.Shutdown();
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        SaveWindowBounds();
        _closing = true;
        _escTimer.Stop();
        _hideTimer.Stop();
        _clockTimer.Stop();
        _toastTimer.Stop();
        _recoveryTimer.Stop();
        DetachPlayerHandlers();
        try { if (Video.MediaPlayer != null) Video.MediaPlayer = null; } catch { }
        if (!_parked)
        {
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            try { _media?.Dispose(); } catch { }
        }
        // Parked: player + media survive — AppState's PiP keeps them playing.
        _player = null;
        _media = null;
    }

    private void DetachPlayerHandlers()
    {
        if (_player == null) return;
        try { _player.Buffering -= OnBuffering; } catch { }
        try { _player.Playing -= OnPlaying; } catch { }
        try { _player.EncounteredError -= OnPlayerError; } catch { }
    }
}
