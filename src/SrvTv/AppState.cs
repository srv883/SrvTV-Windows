using LibVLCSharp.Shared;
using SrvTv.Data;
using SrvTv.Models;

namespace SrvTv;

// Process-wide state: preferences, repositories, and the parked player
// (BACK from fullscreen keeps playing in the grid's mini window).
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public PrefsStore Prefs { get; } = PrefsStore.Default();
    public ChannelRepository Repo { get; }
    public EpgRepository Epg { get; } = new();

    public string SelectedCategoryId = "fav";
    public string? LastOpenedChannelId;

    public LibVLC? Lib;
    public MediaPlayer? ParkedPlayer;
    public Channel? ParkedChannel;
    public string? ParkedCategoryId;
    public string? ParkedCategoryName;

    private AppState()
    {
        Repo = new ChannelRepository(Prefs);
    }

    public async Task InitAsync(Action<string> progress)
    {
        Repo.ProgressChanged = progress;
        await Repo.InitializeAsync().ConfigureAwait(false);
        progress("Loading guide...");
        try { await Epg.RefreshIfNeededAsync(Repo.Channels).ConfigureAwait(false); }
        catch (Exception ex) { Log.Write("EPG refresh failed: " + ex.Message); }
        progress("");
    }

    public void EnsureLib()
    {
        if (Lib != null) return;
        Lib = new LibVLC();
    }

    public List<Channel> CurrentChannels()
    {
        var cats = Repo.Categories;
        var cat = cats.FirstOrDefault(c => c.Id == SelectedCategoryId) ?? cats.FirstOrDefault();
        if (cat == null) return new List<Channel>();
        SelectedCategoryId = cat.Id;
        return Repo.GetChannelsForCategory(cat.Id, cat.Name);
    }

    public void Shutdown()
    {
        try { ParkedPlayer?.Stop(); } catch { }
        try { ParkedPlayer?.Dispose(); } catch { }
        ParkedPlayer = null;
        try { Lib?.Dispose(); } catch { }
        Lib = null;
    }
}
