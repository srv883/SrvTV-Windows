using LibVLCSharp.Shared;
using SrvTv.Models;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SrvTv;

public partial class PlayerWindow : Window
{
    private readonly Channel _channel;
    private readonly string? _catId;
    private readonly string? _catName;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _parked;
    private bool _exitOpen;
    private DateTime _escDownAt;
    private bool _escHoldFired;
    private readonly DispatcherTimer _escTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string[] _aspects = { "", "16:9", "4:3" };
    private int _aspectIdx;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

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
        ChannelName.Text = _channel.Name;
        ChannelMeta.Text = $"{_channel.Category} • {_channel.Language}";
        TickClock();
        _clockTimer.Start();
        ShowOsd();
        try
        {
            AppState.Instance.EnsureLib();
            _player = new MediaPlayer(AppState.Instance.Lib);
            _player.Buffering += (_, e) => Dispatcher.Invoke(() =>
                Buffering.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed);
            _player.Playing += (_, _) => Dispatcher.Invoke(() =>
            {
                Buffering.Visibility = Visibility.Collapsed;
                AppState.Instance.Repo.RecordWatch(_channel.Id);
            });
            _player.EncounteredError += (_, _) => Dispatcher.Invoke(() =>
            {
                Buffering.Visibility = Visibility.Collapsed;
                ShowToast("Stream failed — try another channel (Esc)");
                Log.Write("playback error: " + _channel.Url);
            });
            _media = new Media(AppState.Instance.Lib, _channel.Url, FromType.FromLocation);
            Video.MediaPlayer = _player;
            _player.Play(_media);
            Log.Write("playing: " + _channel.Name);
        }
        catch (Exception ex)
        {
            Log.Write("player start FAILED: " + ex);
            ShowToast("Could not start playback");
        }
    }

    private void TickClock()
    {
        Clock.Text = _player is { IsPlaying: true } && _player.Time > 0 && _player.Length <= 0
            ? "LIVE • " + Fmt(_player.Time)
            : _player != null ? Fmt(_player.Time) : "";
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
        switch (e.Key)
        {
            case Key.Escape:
                if (!e.IsRepeat)
                {
                    _escDownAt = DateTime.UtcNow;
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
            if (Video.MediaPlayer != null) Video.MediaPlayer = null;
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
