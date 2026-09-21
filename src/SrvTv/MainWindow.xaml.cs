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
    private bool _syncingRail;
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _toastTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await BootAsync();
        Activated += (_, _) => SyncPip();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => UpdateClock();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
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
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("h:mm tt");
        ClockDate.Text = now.ToString("ddd, d MMM");
    }

    private void ShowToast(string message)
    {
        Toast.Text = message;
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
        Log.Write($"section: {cat.Id} ({list.Count})");
        _syncingRail = true;
        Rail.SelectedIndex = repo.Categories.FindIndex(c => c.Id == cat.Id);
        Rail.ScrollIntoView(Rail.SelectedItem);
        _syncingRail = false;
        FillRailCounts();
        if (focusTop) FocusGridPosition(0);
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
        var at = fresh.FindIndex(c => c.Id == id);
        if (cat == "fav" && wasFav)
            FocusGridPosition(Math.Min(Math.Max(pos, 0), Math.Max(0, fresh.Count - 1)));
        else
            FocusGridPosition(at >= 0 ? at : 0);
        ShowToast(repo.IsFavorite(id) ? $"{ch.Name} added to Favorites ★" : $"{ch.Name} removed from Favorites");
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PlaySelected();
    }

    private void PlaySelected()
    {        var pos = Grid.SelectedIndex;
        var list = GridChannels;
        if (pos < 0 || pos >= list.Count) return;
        var ch = list[pos];
        AppState.Instance.LastOpenedChannelId = ch.Id;
        HidePip();
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

        var inRail = Rail.IsKeyboardFocusWithin;
        var inGrid = Grid.IsKeyboardFocusWithin;

        if (e.Key == Key.Escape) { ShowExitOverlay(); e.Handled = true; return; }
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

    private void OnSearch(object sender, RoutedEventArgs e) => new SearchWindow().Show();
    private void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow().Show();

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
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
        catch (Exception ex) { Log.Write("refresh failed: " + ex.Message); }
        await Dispatcher.InvokeAsync(() => Loading.Visibility = Visibility.Collapsed);
    }

    private void SyncPip()
    {
        var p = AppState.Instance.ParkedPlayer;
        if (p == null) { HidePip(); return; }
        try
        {
            MiniView.MediaPlayer = p;
            PipFrame.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Write("pip attach failed: " + ex.Message);
            HidePip();
        }
    }

    private void HidePip()
    {
        try { MiniView.MediaPlayer = null; } catch { }
        PipFrame.Visibility = Visibility.Collapsed;
    }

    private void OnPipClose(object sender, RoutedEventArgs e)
    {
        try { AppState.Instance.ParkedPlayer?.Stop(); } catch { }
        try { AppState.Instance.ParkedPlayer?.Dispose(); } catch { }
        AppState.Instance.ParkedPlayer = null;
        AppState.Instance.ParkedChannel = null;
        HidePip();
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
