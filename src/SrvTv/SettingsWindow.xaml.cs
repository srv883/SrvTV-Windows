using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace SrvTv;

public partial class SettingsWindow : Window
{
    private bool _findingLinks;

    public SettingsWindow()
    {
        InitializeComponent();
        if (Application.Current.MainWindow is { IsLoaded: true } main && !ReferenceEquals(main, this))
            Owner = main;
        UiTest.Place(this, 720, 600);
        VersionText.Text = $"Srv TV {Updater.CurrentText}";
        if (!Updater.Enabled) UpdateStatus.Text = "Updates are on for copies installed with SrvTV-Setup.exe.";
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        UpdateStatus.Text = "Checking...";
        try { UpdateStatus.Text = await Updater.CheckAsync(); }
        catch (Exception ex) { UpdateStatus.Text = "Check failed: " + ex.Message; }
        BtnCheckUpdate.IsEnabled = true;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        Status.Text = "Refreshing...";
        try
        {
            await AppState.Instance.Repo.RefreshAllPlaylistsAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
                Status.Text = $"{AppState.Instance.Repo.Channels.Count} channels loaded.");
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => Status.Text = "Failed: " + ex.Message);
            Log.Write("manual refresh failed: " + ex);
        }
        await Dispatcher.InvokeAsync(() => BtnRefresh.IsEnabled = true);
    }

    private async void OnFindLinks(object sender, RoutedEventArgs e)
    {
        var repo = AppState.Instance.Repo;
        if (_findingLinks || repo.FindLinksRunning)
        {
            repo.CancelFindLinks();
            FindLinksStatus.Text = "Stopping after the current checks…";
            return;
        }
        _findingLinks = true;
        BtnFindLinks.Content = "Stop";
        FindLinksStatus.Text = "Starting link check…";
        Action<int, int, int> progress = (tested, total, repaired) =>
            Dispatcher.InvokeAsync(() => FindLinksStatus.Text = total > 0
                ? $"Checking {tested} of {total}…  ·  {repaired} repaired"
                : "Checking links…");
        repo.FindLinksProgress += progress;
        try
        {
            var result = await repo.FindWorkingLinksAsync().ConfigureAwait(false);
            var message = result.Total == 0 ? "Nothing to check yet."
                : $"Checked {result.Tested} channels · {result.Repaired} repaired" +
                  (result.Dead > 0 ? $" · {result.Dead} still dead" : "");
            await Dispatcher.InvokeAsync(() =>
            {
                FindLinksStatus.Text = message;
                if (Application.Current.MainWindow is MainWindow main) main.ReloadGrid();
            });
            Log.Write("find working links: " + message);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => FindLinksStatus.Text = "Link check failed: " + ex.Message);
        }
        finally
        {
            repo.FindLinksProgress -= progress;
            _findingLinks = false;
            await Dispatcher.InvokeAsync(() => BtnFindLinks.Content = "Find links");
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<HashSet<string>>(FavJson.Text.Trim());
            if (ids == null || ids.Count == 0) { Status.Text = "Nothing to import."; return; }
            var repo = AppState.Instance.Repo;
            var known = repo.Channels.Select(c => c.Id).ToHashSet();
            var added = 0;
            foreach (var id in ids)
            {
                if (!known.Contains(id)) continue;
                if (!repo.IsFavorite(id)) { repo.ToggleFavorite(id); added++; }
            }
            repo.RefreshFavorites();
            var matched = ids.Count(known.Contains);
            Status.Text = $"Imported {added} favorites ({matched - added} already starred, {ids.Count - matched} unknown).";
            Log.Write($"favorites import: +{added}");
        }
        catch (Exception ex)
        {
            Status.Text = "Import failed: paste a JSON array like [\"id1\",\"id2\"].";
            Log.Write("import failed: " + ex.Message);
        }
    }

    private void OnLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
