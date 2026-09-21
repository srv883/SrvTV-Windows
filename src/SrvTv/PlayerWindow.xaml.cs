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
    private readonly string[] _aspects = { "", "16:9", "4:3" };
    private int _aspectIdx;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

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
        Loaded += (_, _) => StartPlayback();
        Closed += (_, _) => Cleanup();
        _escTimer.Tick += (_, _) =>
        {
            _escTimer.Stop();
            _escHoldFired = true;
            ShowExitOverlay();
        };
        _hideTimer.Tick += (_, _) => { InfoBar.Visibility = Visibility.Collapsed; Hint.Visibility = Visibility.Collapsed; _hideTimer.Stop(); };
        _clockTimer.Tick += (_, _) => TickClock();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
    }

    private void StartPlayback()
    {
        try
        {
            AppState.Instance.EnsureLib();
            _player = new MediaPlayer(AppState.Instance.Lib);
            _player.Buffering += OnBuffering;
            _player.Playing += OnPlaying;
            _player.EncounteredError += OnPlayerError;
            Video.MediaPlayer = _player;
        }
        catch (Exception ex)
        {
            Log.Write("player start FAILED: " + ex);
            ShowToast("Could not start playback");
            return;
        }
        PlayChannel(_channel);
    }

    private void PlayChannel(Channel channel)
    {
        if (_player == null) return;
        try { _player.Stop(); } catch { }
        try { _media?.Dispose(); } catch { }
        _media = null;
        _channel = channel;
        ChannelName.Text = channel.Name;
        ChannelMeta.Text = $"{channel.Category} • {channel.Language}";
        UpdateEpgLine();
        TickClock();
        _clockTimer.Start();
        ShowOsd();
        try
        {
            _media = new Media(AppState.Instance.Lib, channel.Url, FromType.FromLocation);
            _player.Play(_media);
            Log.Write("playing: " + channel.Name);
        }
        catch (Exception ex)
        {
            Log.Write("play channel FAILED: " + ex);
            ShowToast("Could not start playback");
        }
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) => Dispatcher.Invoke(() =>
        Buffering.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed);

    private void OnPlaying(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        Buffering.Visibility = Visibility.Collapsed;
        AppState.Instance.Repo.RecordWatch(_channel.Id);
    });

    private void OnPlayerError(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        Buffering.Visibility = Visibility.Collapsed;
        ShowToast("Stream failed — try another channel (Esc)");
        Log.Write("playback error: " + _channel.Url);
    });

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
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ShowToast(string message)
    {
        Toast.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ShowOsd();
        if (_exitOpen)
        {
            if (e.Key == Key.Escape) { HideExitOverlay(); e.Handled = true; }
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
                case Key.Up:
                case Key.Down:
                    return; // rows navigate natively
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
            var meta = ch.Category;
            if (ch.Language.Length > 0) meta += " • " + ch.Language;
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
        GuideHeader.Text = (_catName ?? "All Channels").ToUpperInvariant();
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

    private void ShowExitOverlay()
    {
        _exitOpen = true;
        ExitOverlay.Visibility = Visibility.Visible;
        BtnExitNo.Focus();
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
        _escTimer.Stop();
        _hideTimer.Stop();
        _clockTimer.Stop();
        _toastTimer.Stop();
        try { if (Video.MediaPlayer != null) Video.MediaPlayer = null; } catch { }
        if (!_parked)
        {
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            try { _media?.Dispose(); } catch { }
        }
        else
        {
            try { _media?.Dispose(); } catch { }
        }
        _player = null;
        _media = null;
    }
}
