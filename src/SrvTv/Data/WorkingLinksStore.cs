// Srv TV for Windows — links learned by "Find working links".
// Own file, keyed by the stable channel id (with a normalized-name fallback),
// so a repair never touches favorites, watch history or the channel cache.
// Same shape as Android's WorkingLinksStore and web/data/working_links.json.
using System.Text.Json;

namespace SrvTv.Data;

public sealed class WorkingLink
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    public string OriginalUrl { get; set; } = "";
    public string Url { get; set; } = "";
    public string Reason { get; set; } = "";
    public long At { get; set; }
}

public sealed class WorkingLinksStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, WorkingLink> _links = new(StringComparer.Ordinal);

    public WorkingLinksStore(string path)
    {
        _path = path;
        Load();
    }

    public static WorkingLinksStore Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");
        Directory.CreateDirectory(dir);
        var name = UiTest.On ? "working_links-uitest.json" : "working_links.json";
        return new WorkingLinksStore(Path.Combine(dir, name));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, WorkingLink>>(
                File.ReadAllText(_path), JsonOpts);
            if (loaded != null)
                _links = new Dictionary<string, WorkingLink>(loaded, StringComparer.Ordinal);
        }
        catch { /* unreadable overrides: fall back to the feed's own URLs */ }
    }

    public Dictionary<string, WorkingLink> Read()
    {
        lock (_gate) return new Dictionary<string, WorkingLink>(_links, StringComparer.Ordinal);
    }

    public void Write(Dictionary<string, WorkingLink> links)
    {
        lock (_gate)
        {
            _links = new Dictionary<string, WorkingLink>(links, StringComparer.Ordinal);
            try
            {
                var json = JsonSerializer.Serialize(_links, JsonOpts);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Log.Write("working links write failed: " + ex.Message); }
        }
    }
}
