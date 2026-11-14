using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SrvTv.Data;
using SrvTv.Models;

namespace SrvTv;

public partial class MainWindow : Window
{
    private const double CardStep = 304.0; // 290 card + 2x7 container padding
    private bool _interacted;
    private bool _refreshing;
    private bool _syncingRail;
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _toastTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        UiTest.Place(this);
        UiTest.Attach(this, "main", (verb, arg) =>
        {
            if (verb == "zone") UiTest.Zone = arg;
            if (verb == "search") OpenSearch(arg);
            if (verb == "settings") new SettingsWindow().Show();
            if (verb == "play" && AppState.Instance.Repo.SearchChannels(arg).FirstOrDefault() is { } ch)
                PlayChannel(ch);
        });
        Loaded += async (_, _) => await BootAsync();
        SizeChanged += (_, _) =>
        {
            if (PipFrame.Visibility == Visibility.Visible) PlacePip();
        };
        Activated += (_, _) => { SyncPip(); RefreshNowChips(); };
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => UpdateClock();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        Grid.AddHandler(ChannelCard.PlayRequestedEvent, new RoutedEventHandler(OnCardPlayRequested));
        Grid.AddHandler(ChannelCard.FavoriteToggledEvent, new RoutedEventHandler(OnCardFavoriteToggled));
        Rail.PreviewMouseWheel += OnRailPreviewMouseWheel;
        MiniView.Loaded += (_, _) =>
        {
            if (AppState.Instance.ParkedPlayer != null) AttachParked();
        };
    }

    private async Task BootAsync()
    {
        UpdateClock();
        _clock.Start();
        try
        {
            await AppState.Instance.InitAsync(m => Dispatcher.Invoke(() =>
            {
                if (m.Length > 0) { Loading.Visibility = Visibility.Visible; LoadingText.Text = m; }
                else Loading.Visibility = Visibility.Collapsed;
            })).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Write("Init failed: " + ex); }
        await Dispatcher.InvokeAsync(() =>
        {
            Loading.Visibility = Visibility.Collapsed;
            FillRail();
            LoadCategory(AppState.Instance.SelectedCategoryId, focusTop: false);
            ChannelCount.Text = $"{AppState.Instance.Repo.Channels.Count} channels";
            if (GridChannels.Count == 0)
                FocusRailPill(); // empty section (e.g. fresh boot, no favorites yet)
            else
                FocusGridPosition(0);
            Log.Write($"boot done: {AppState.Instance.Repo.Channels.Count} channels, " +
                      $"{AppState.Instance.Repo.Categories.Count} categories");
        });

        // Recheck stale manifests after the cached grid is visible. A
        // changed playlist can rebuild the grid, but never delays first paint.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await AppState.Instance.Repo.BootRecheckAsync().ConfigureAwait(false))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        FillRail();
                        LoadCategory(AppState.Instance.SelectedCategoryId, focusTop: false);
                        ChannelCount.Text = $"{AppState.Instance.Repo.Channels.Count} channels";
                    });
                }
            }
            catch (Exception ex) { Log.Write("boot recheck failed: " + ex.Message); }
        });
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("h:mm tt");
        ClockDate.Text = now.ToString("ddd, d MMM");
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ----- rail -----

    private void FillRail()
    {
        _syncingRail = true;
        Rail.ItemsSource = AppState.Instance.Repo.Categories;
        Rail.SelectedIndex = Math.Max(0, AppState.Instance.Repo.Categories
            .FindIndex(c => c.Id == AppState.Instance.SelectedCategoryId));
        _syncingRail = false;
    }

    private void OnRailSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRail) return;
        if (Rail.SelectedItem is Category cat && cat.Id != AppState.Instance.SelectedCategoryId)
            LoadCategory(cat.Id, focusTop: true);
    }

    private void StepPillFocus(int delta)
    {
        var count = Rail.Items.Count;
        if (count == 0) return;
        var target = Math.Clamp(Rail.SelectedIndex + delta, 0, count - 1);
        Rail.SelectedIndex = target; // SelectionChanged loads the section
        Rail.ScrollIntoView(Rail.SelectedItem);
        FocusRailPill(target);
    }

    private void FocusRailPill(int? index = null)
    {
        var target = index ?? Rail.SelectedIndex;
        UiTest.Zone = "rail";
        Dispatcher.BeginInvoke(() =>
        {
            if (Rail.ItemContainerGenerator.ContainerFromIndex(target) is ListBoxItem it) it.Focus();
        });
    }

    // ----- grid -----

    private List<Channel> GridChannels => (Grid.ItemsSource as List<Channel>) ?? new List<Channel>();

    private void LoadCategory(string id, bool focusTop)
    {
        var repo = AppState.Instance.Repo;
        if (repo.Categories.Count == 0) return;
        var cat = repo.Categories.FirstOrDefault(c => c.Id == id) ?? repo.Categories[0];
        AppState.Instance.SelectedCategoryId = cat.Id;
        var list = repo.GetChannelsForCategory(cat.Id, cat.Name);
        Grid.ItemsSource = list;
        UpdateEmptyState(cat, list.Count);
        Log.Write($"section: {cat.Id} ({list.Count})");
        _syncingRail = true;
        Rail.SelectedIndex = repo.Categories.FindIndex(c => c.Id == cat.Id);
        Rail.ScrollIntoView(Rail.SelectedItem);
        _syncingRail = false;
        FillRailCounts();
        if (focusTop) FocusGridPosition(0);
    }

    private void UpdateEmptyState(Category cat, int count)
    {
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 0) return;
        if (cat.Id == "fav")
        {
            EmptyTitle.Text = "No favorites yet";
            EmptyText.Text = "Press F on a channel, or click the star on its card, to keep it here.";
        }
        else
        {
            EmptyTitle.Text = $"Nothing in {cat.DisplayName} right now";
            EmptyText.Text = "Pick another section with ← → or refresh the channel lists.";
        }
    }

    private void FillRailCounts()
    {
        // Refresh counts (favorites size changes with toggles).
        _syncingRail = true;
        var sel = Rail.SelectedIndex;
        Rail.ItemsSource = AppState.Instance.Repo.Categories;
        Rail.SelectedIndex = sel;
        _syncingRail = false;
    }

    private int GridSpan()
    {
        var sv = FindScrollViewer(Grid);
        var w = sv?.ViewportWidth ?? Grid.ActualWidth;
        if (w <= 0) w = 1200;
        return Math.Max(1, (int)(w / CardStep));
    }

    private int GridPageLines()
    {
        var sv = FindScrollViewer(Grid);
        var h = sv?.ViewportHeight ?? Grid.ActualHeight;
        if (h <= 0) h = 600;
        return Math.Max(1, (int)(h / 182)); // 168 card + 14 container padding
    }

    private void OnRailPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        StepPillFocus(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void FocusGridPosition(int position)
    {
        var items = GridChannels;
        if (items.Count == 0) return;
        position = Math.Clamp(position, 0, items.Count - 1);
        UiTest.Zone = "grid";
        Grid.SelectedIndex = position;
        Grid.ScrollIntoView(Grid.SelectedItem);
        Dispatcher.BeginInvoke(() =>
        {
            if (Grid.ItemContainerGenerator.ContainerFromIndex(position) is ListBoxItem it) it.Focus();
            else Dispatcher.BeginInvoke(() =>
            {
                if (Grid.ItemContainerGenerator.ContainerFromIndex(position) is ListBoxItem it2) it2.Focus();
                else Grid.Focus(); // never strand keyboard focus in the void
            });
        });
    }

    private bool StepGridFocusH(int delta)
    {
        var pos = Grid.SelectedIndex;
        if (pos < 0) return false;
        var span = GridSpan();
        var targetCol = pos % span + delta;
        if (targetCol < 0 || targetCol >= span) return false;
        var target = pos + delta;
        if (target < 0 || target >= GridChannels.Count) return false;
        FocusGridPosition(target);
        return true;
    }

    private bool StepGridFocus(int delta)
    {
        var pos = Grid.SelectedIndex;
        if (pos < 0) return false;
        var target = Math.Clamp(pos + delta, 0, GridChannels.Count - 1);
        if (target == pos) return false;
        FocusGridPosition(target);
        return true;
    }

    private int GridColumn()
    {
        var pos = Grid.SelectedIndex;
        return pos < 0 ? -1 : pos % GridSpan();
    }

    private void MoveToSection(int delta)
    {
        var cats = AppState.Instance.Repo.Categories;
        if (cats.Count == 0) return;
        var idx = cats.FindIndex(c => c.Id == AppState.Instance.SelectedCategoryId);
        if (idx < 0) idx = 0;
        var target = idx + delta;
        if (target < 0 || target >= cats.Count) return;
        LoadCategory(cats[target].Id, focusTop: false);
        FocusGridPosition(delta < 0 ? GridChannels.Count - 1 : 0);
    }

    private void ToggleFocusedFavorite()
    {
        var pos = Grid.SelectedIndex;
        var list = GridChannels;
        if (pos < 0 || pos >= list.Count) return;
        var ch = list[pos];
        if (Data.OfficialApps.IsShortcut(ch)) return;
        var repo = AppState.Instance.Repo;
        var wasFav = repo.IsFavorite(ch.Id);
        repo.ToggleFavorite(ch.Id);
        repo.RefreshFavorites();
        FillRailCounts();
        ChannelCount.Text = $"{repo.Channels.Count} channels";
        // Reload section (cheap + keeps counts right), restore focus by id.
        var id = ch.Id;
        var cat = AppState.Instance.SelectedCategoryId;
        var fresh = repo.GetChannelsForCategory(cat,
            repo.Categories.FirstOrDefault(c => c.Id == cat)?.Name ?? "");
        Grid.ItemsSource = fresh;
        if (repo.Categories.FirstOrDefault(c => c.Id == cat) is { } cur) UpdateEmptyState(cur, fresh.Count);
        var at = fresh.FindIndex(c => c.Id == id);
        if (cat == "fav" && wasFav)
            FocusGridPosition(Math.Min(Math.Max(pos, 0), Math.Max(0, fresh.Count - 1)));
        else
            FocusGridPosition(at >= 0 ? at : 0);
        ShowToast(repo.IsFavorite(id) ? $"{ch.Name} added to Favorites ★" : $"{ch.Name} removed from Favorites");
    }

    private void OnCardPlayRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ChannelCard card && card.DataContext is Channel ch)
            PlayChannel(ch);
    }

    private void OnCardFavoriteToggled(object sender, RoutedEventArgs e)
    {
        var repo = AppState.Instance.Repo;
        repo.RefreshFavorites();
        FillRailCounts();
        ChannelCount.Text = $"{repo.Channels.Count} channels";
        // Rebuild the current section so fav counts/order stay correct;
        // restore focus to the toggled channel if still present.
        var list = GridChannels;
        var id = (e.OriginalSource as ChannelCard)?.DataContext is Channel c ? c.Id : null;
        var cat = AppState.Instance.SelectedCategoryId;
        var fresh = repo.GetChannelsForCategory(cat,
            repo.Categories.FirstOrDefault(x => x.Id == cat)?.Name ?? "");
        Grid.ItemsSource = fresh;
        if (repo.Categories.FirstOrDefault(x => x.Id == cat) is { } cur) UpdateEmptyState(cur, fresh.Count);
        var at = id != null ? fresh.FindIndex(x => x.Id == id) : -1;
        FocusGridPosition(at >= 0 ? at : Math.Min(Grid.SelectedIndex, Math.Max(0, fresh.Count - 1)));
    }

    private void PlaySelected()
    {
        var pos = Grid.SelectedIndex;
        var list = GridChannels;
        if (pos < 0 || pos >= list.Count) return;
        PlayChannel(list[pos]);
    }

    private void PlayChannel(Channel ch)
    {
        if (Data.OfficialApps.IsShortcut(ch))
        {
            Data.OfficialApps.Open(ch);
            ShowToast($"Opening {ch.Name} in your browser");
            return;
        }
        AppState.Instance.LastOpenedChannelId = ch.Id;
        AppState.Instance.ReleaseParked(); // never two streams at once
        HidePip();
        ch = AppState.Instance.Repo.GetChannelById(ch.Id) ?? ch;
        var cat = AppState.Instance.Repo.Categories
            .FirstOrDefault(c => c.Id == AppState.Instance.SelectedCategoryId);
        new PlayerWindow(ch, cat?.Id, cat?.Name).Show();
    }

    // ----- keyboard -----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ExitOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { HideExitOverlay(); e.Handled = true; }
            return; // buttons navigate natively
        }
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Enter or Key.Escape or Key.F) _interacted = true;

        var inRail = UiTest.FocusWithin(Rail, "rail");
        var inGrid = UiTest.FocusWithin(Grid, "grid");

        if (e.Key == Key.Escape) { ShowExitOverlay(); e.Handled = true; return; }
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnSearch(this, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (e.Key == Key.F && inGrid) { ToggleFocusedFavorite(); e.Handled = true; return; }

        if (inGrid)
        {
            switch (e.Key)
            {
                case Key.Up:
                    if (Grid.SelectedIndex < GridSpan()) { Rail.Focus(); StepPillFocus(0); }
                    else StepGridFocus(-GridSpan());
                    e.Handled = true; return;
                case Key.Down:
                    StepGridFocus(GridSpan());
                    e.Handled = true; return;
                case Key.PageUp:
                    StepGridFocus(-GridSpan() * GridPageLines());
                    e.Handled = true; return;
                case Key.PageDown:
                    StepGridFocus(GridSpan() * GridPageLines());
                    e.Handled = true; return;
                case Key.Home:
                    FocusGridPosition(0);
                    e.Handled = true; return;
                case Key.End:
                    FocusGridPosition(GridChannels.Count - 1);
                    e.Handled = true; return;
                case Key.Left:
                    if (!StepGridFocusH(-1)) MoveToSection(-1);
                    e.Handled = true; return;
                case Key.Right:
                    if (!StepGridFocusH(1)) MoveToSection(1);
                    e.Handled = true; return;
                case Key.Enter:
                    PlaySelected();
                    e.Handled = true; return;
            }
        }
        else if (inRail)
        {
            switch (e.Key)
            {
                case Key.Left: StepPillFocus(-1); e.Handled = true; return;
                case Key.Right: StepPillFocus(1); e.Handled = true; return;
                case Key.Down:
                    FocusGridPosition(Math.Min(GridColumn() < 0 ? 0 : GridColumn(), GridChannels.Count - 1));
                    e.Handled = true; return;
                case Key.Enter:
                    if (Rail.SelectedItem is Category)
                    {
                        LoadCategory(AppState.Instance.SelectedCategoryId, focusTop: true);
                        e.Handled = true;
                    }
                    return;
            }
        }
    }

    // ----- header buttons / PiP / exit -----

    private void OnSearch(object sender, RoutedEventArgs e) => OpenSearch();
    public void OpenSearch(string? query = null)
    {
        var w = new SearchWindow();
        if (!string.IsNullOrEmpty(query)) w.Prefill(query);
        w.Show();
    }
    private void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow().Show();

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _refreshing = true;
        BtnRefresh.IsEnabled = false;
        Loading.Visibility = Visibility.Visible;
        LoadingText.Text = "Refreshing channels...";
        try
        {
            await AppState.Instance.Repo.RefreshAllPlaylistsAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                FillRail();
                LoadCategory(AppState.Instance.SelectedCategoryId, focusTop: false);
                ChannelCount.Text = $"{AppState.Instance.Repo.Channels.Count} channels";
            });
        }
        catch (Exception ex)
        {
            Log.Write("refresh failed: " + ex.Message);
            await Dispatcher.InvokeAsync(() => ShowToast("Refresh failed — keeping existing channels"));
        }
        finally
        {
            _refreshing = false;
            await Dispatcher.InvokeAsync(() =>
            {
                Loading.Visibility = Visibility.Collapsed;
                BtnRefresh.IsEnabled = true;
            });
        }
    }

    private void RefreshNowChips()
    {
        try
        {
            for (var i = 0; i < Grid.Items.Count; i++)
            {
                if (Grid.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem it
                    && FindVisualChild<ChannelCard>(it) is { } card)
                    card.RefreshNowChip();
            }
        }
        catch { }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    internal void SyncPip()
    {
        var p = AppState.Instance.ParkedPlayer;
        if (p == null) { HidePip(); return; }
        // The VideoView needs a real HWND before a player can render into
        // it: attach on the Loaded pass (after this frame is laid out).
        PipTitle.Text = AppState.Instance.ParkedChannel?.Name ?? "";
        PipFrame.Visibility = Visibility.Visible;
        PlacePip();
        AttachParked();
    }

    private void AttachParked()
    {
        var p = AppState.Instance.ParkedPlayer;
        if (p == null) { HidePip(); return; }
        if (ReferenceEquals(MiniView.MediaPlayer, p))
        {
            if (!p.IsPlaying) p.Play();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                MiniView.MediaPlayer = p;
                if (!p.IsPlaying) p.Play();
                else RebindVideo(p);
                Log.Write("pip attached: " + (AppState.Instance.ParkedChannel?.Name ?? "?"));
            }
            catch (Exception ex)
            {
                Log.Write("pip attach failed: " + ex.Message);
                HidePip();
            }
        }));
    }

    // VLC keeps drawing into the window the picture started in (the closed
    // full player), so the mini player would stay black with sound only.
    // Switching the video track off and on makes VLC open a new video
    // output in the mini player's window, without reconnecting the stream.
    private void RebindVideo(LibVLCSharp.Shared.MediaPlayer p)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(MiniView.MediaPlayer, p)) return;
            try
            {
                var track = p.VideoTrack;
                if (track < 0 && p.VideoTrackCount > 0)
                    track = p.VideoTrackDescription.Select(t => t.Id).FirstOrDefault(id => id >= 0);
                if (track >= 0)
                {
                    p.SetVideoTrack(-1);
                    p.SetVideoTrack(track);
                    Log.Write($"pip video rebound (track {track})");
                }
                else
                {
                    p.Stop();
                    p.Play();
                    Log.Write("pip video restarted");
                }
            }
            catch (Exception ex) { Log.Write("pip rebind failed: " + ex.Message); }
        };
        timer.Start();
    }

    private void HidePip()
    {
        try { MiniView.MediaPlayer = null; } catch { }
        PipFrame.Visibility = Visibility.Collapsed;
    }

    private void OnPipClose(object sender, RoutedEventArgs e)
    {
        AppState.Instance.ReleaseParked();
        AppState.Instance.ParkedChannel = null;
        HidePip();
    }

    // ----- mini player position, size and dragging -----

    private static readonly double[] PipWidths = { 320, 440, 560 };
    private static int _pipSizeIdx;
    private static Point? _pipPos; // top-left inside the window; null = default corner
    private Point _dragStart;
    private Point _dragOrigin;
    private bool _dragging;
    private bool _dragMoved;

    private void PlacePip()
    {
        var w = PipWidths[_pipSizeIdx];
        PipFrame.Width = w;
        PipVideoBox.Height = Math.Round(w * 9 / 16);
        var h = PipVideoBox.Height + 34 + 3;
        var pos = _pipPos ?? new Point(Root.ActualWidth - w - 28, Root.ActualHeight - h - 56);
        MovePip(pos);
    }

    private void MovePip(Point pos)
    {
        var w = PipFrame.Width;
        var h = PipVideoBox.Height + 37;
        var maxX = Math.Max(0, Root.ActualWidth - w - 8);
        var maxY = Math.Max(0, Root.ActualHeight - h - 8);
        var x = Math.Clamp(pos.X, 8, Math.Max(8, maxX));
        var y = Math.Clamp(pos.Y, 8, Math.Max(8, maxY));
        PipFrame.Margin = new Thickness(x, y, 0, 0);
    }

    private void OnPipDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IInputElement el) return;
        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(Root);
        _dragOrigin = new Point(PipFrame.Margin.Left, PipFrame.Margin.Top);
        el.CaptureMouse();
        e.Handled = true;
    }

    private void OnPipDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = e.GetPosition(Root);
        var dx = now.X - _dragStart.X;
        var dy = now.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _dragMoved = true;
        MovePip(new Point(_dragOrigin.X + dx, _dragOrigin.Y + dy));
    }

    private void OnPipDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        (sender as IInputElement)?.ReleaseMouseCapture();
        e.Handled = true;
        if (_dragMoved)
        {
            _pipPos = new Point(PipFrame.Margin.Left, PipFrame.Margin.Top);
            Activate(); // a click on the video overlay can take activation; give it back
        }
        else if (ReferenceEquals(sender, PipBar)) Activate();
        else ResumeFromPip(); // plain click on the picture = back to the full player
    }

    private void OnPipSize(object sender, RoutedEventArgs e)
    {
        // Keep the bottom-right corner where it is while the size changes.
        var oldW = PipFrame.Width;
        var oldH = PipVideoBox.Height + 37;
        var right = PipFrame.Margin.Left + oldW;
        var bottom = PipFrame.Margin.Top + oldH;
        _pipSizeIdx = (_pipSizeIdx + 1) % PipWidths.Length;
        var w = PipWidths[_pipSizeIdx];
        PipFrame.Width = w;
        PipVideoBox.Height = Math.Round(w * 9 / 16);
        MovePip(new Point(right - w, bottom - (PipVideoBox.Height + 37)));
        _pipPos = new Point(PipFrame.Margin.Left, PipFrame.Margin.Top);
    }

    private void OnPipExpand(object sender, RoutedEventArgs e) => ResumeFromPip();

    // Back to the full player on the same channel (fresh pipeline; the
    // parked player is released first).
    private void ResumeFromPip()
    {
        var ch = AppState.Instance.ParkedChannel;
        if (ch == null) return;
        var catId = AppState.Instance.ParkedCategoryId;
        var catName = AppState.Instance.ParkedCategoryName;
        AppState.Instance.ReleaseParked();
        HidePip();
        AppState.Instance.LastOpenedChannelId = ch.Id;
        new PlayerWindow(ch, catId, catName).Show();
    }

    private void ShowExitOverlay()
    {
        ExitOverlay.Visibility = Visibility.Visible;
        BtnExitNo.Focus();
    }

    private void HideExitOverlay() => ExitOverlay.Visibility = Visibility.Collapsed;
    private void OnExitNo(object sender, RoutedEventArgs e) => HideExitOverlay();

    private void OnExitYes(object sender, RoutedEventArgs e)
    {
        AppState.Instance.Shutdown();
        Application.Current.Shutdown();
    }
}
