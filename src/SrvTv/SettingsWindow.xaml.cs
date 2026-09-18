using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace SrvTv;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
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
