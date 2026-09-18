// Srv TV for Windows — key/value preference store.
// Replaces Android SharedPreferences with a single JSON file under
// %LocalAppData%\SrvTv. Same string/long/int semantics as the app.
using System.Text.Json;

namespace SrvTv.Data;

public sealed class PrefsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _map = new();
    private readonly object _gate = new();

    public PrefsStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (loaded != null)
                    foreach (var kv in loaded) _map[kv.Key] = kv.Value;
            }
        }
        catch { /* corrupt prefs: start clean, never crash boot */ }
    }

    public static PrefsStore Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");
        Directory.CreateDirectory(dir);
        return new PrefsStore(Path.Combine(dir, "prefs.json"));
    }

    public string GetString(string key, string def = "")
    {
        lock (_gate) return _map.TryGetValue(key, out var v) ? v : def;
    }

    public void PutString(string key, string value)
    {
        lock (_gate) _map[key] = value;
    }

    public long GetLong(string key, long def = 0L)
    {
        lock (_gate)
            return _map.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : def;
    }

    public void PutLong(string key, long value)
    {
        lock (_gate) _map[key] = value.ToString();
    }

    public int GetInt(string key, int def = 0)
    {
        lock (_gate)
            return _map.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;
    }

    public void PutInt(string key, int value)
    {
        lock (_gate) _map[key] = value.ToString();
    }

    public void Apply()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_map);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
