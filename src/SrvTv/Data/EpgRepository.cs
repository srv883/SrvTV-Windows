// Srv TV for Windows — XMLTV guide repository.
// Port of the Android EpgRepository: small per-region guides, streaming
// parse, dotted-id + subset matching with fan-out, JSON cache.
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using SrvTv.Models;

namespace SrvTv.Data;

public sealed class EpgProgramme
{
    public string ChannelId { get; set; } = "";
    public long StartMs { get; set; }
    public long StopMs { get; set; }
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";

    public string ClockLabel()
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(StartMs).ToLocalTime().ToString("HH:mm"); }
        catch { return ""; }
    }

    public string RangeLabel()
    {
        try
        {
            var s = DateTimeOffset.FromUnixTimeMilliseconds(StartMs).ToLocalTime().ToString("HH:mm");
            var e = DateTimeOffset.FromUnixTimeMilliseconds(StopMs).ToLocalTime().ToString("HH:mm");
            return $"{s} - {e}";
        }
        catch { return ""; }
    }
}

public sealed class EpgRepository
{
    private readonly string _cachePath;
    private Dictionary<string, List<EpgProgramme>> _programs = new();
    private long _fetchedAt;

    private const long MaxAgeMs = 12L * 60 * 60 * 1000;
    private const long WindowPastMs = 3L * 60 * 60 * 1000;
    private const long WindowFutureMs = 36L * 60 * 60 * 1000;
    private const int MaxPerChannel = 80;
    private const int MaxTotal = 40000;

    private static readonly string[] Sources =
    {
        "https://epgshare01.online/epgshare01/epg_ripper_IN1.xml.gz",
        "https://epgshare01.online/epgshare01/epg_ripper_IN4.xml.gz",
        "https://epgshare01.online/epgshare01/epg_ripper_IN2.xml.gz",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public EpgRepository()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "epg.json");
    }

    public static string NormalizeName(string s)
    {
        var t = s.ToLowerInvariant();
        t = Regex.Replace(t, @"\(.*?\)", " ");
        t = Regex.Replace(t, @"\[.*?\]", " ");
        t = Regex.Replace(t, @"\b(uhd|fhd|hd|sd|4k|hdr|hlg|dolby|\+1|timeshift|apac)\b", " ");
        t = Regex.Replace(t, @"[^a-z0-9]+", "");
        return t;
    }

    public static HashSet<string> WordTokens(string name)
    {
        var t = name.ToLowerInvariant();
        t = Regex.Replace(t, @"\(.*?\)", " ");
        t = Regex.Replace(t, @"\[.*?\]", " ");
        t = Regex.Replace(t, @"\b(uhd|fhd|hd|sd|4k|hdr|hlg|dolby|\+1|timeshift|apac|plus|tv|channel)\b", " ");
        return new HashSet<string>(Regex.Split(t, @"[^a-z0-9]+").Where(x => x.Length > 0));
    }

    private static bool SubsetHit(HashSet<string> small, HashSet<string> big)
    {
        if (small.Count == 0 || big.Count == 0) return false;
        if (small.SetEquals(big)) return true;
        if (small.Count == 1)
        {
            if (small.First().Length < 5) return false;
        }
        else if (small.Count < 2) return false;
        return small.All(big.Contains);
    }

    private static string FlatId(string s)
    {
        var t = s.ToLowerInvariant().Trim();
        var at = t.IndexOf('@');
        if (at > 0) t = t[..at];
        return Regex.Replace(t, @"[^a-z0-9]+", "");
    }

    private static string BaseId(string tvgId)
    {
        var t = tvgId.Trim();
        var at = t.IndexOf('@');
        return at > 0 ? t[..at] : t;
    }

    public bool HasData() => _programs.Count > 0;

    public (EpgProgramme? now, EpgProgramme? next) NowNext(string channelId, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_programs.TryGetValue(channelId, out var list) || list.Count == 0)
            return (null, null);
        var nowProg = list.FindLast(p => p.StartMs <= now && now < p.StopMs)
            ?? list.FindLast(p => p.StartMs <= now);
        var nextProg = list.FirstOrDefault(p => p.StartMs > now)
            ?? list.FirstOrDefault(p => p.StopMs > now && p != nowProg);
        return (nowProg, nextProg);
    }

    public List<EpgProgramme> Schedule(string channelId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_programs.TryGetValue(channelId, out var list)) return new List<EpgProgramme>();
        return list.Where(p => p.StopMs > now - WindowPastMs).Take(60).ToList();
    }

    public async Task RefreshIfNeededAsync(List<Channel> channels)
    {
        var cached = await Task.Run(LoadCache).ConfigureAwait(false);
        if (cached && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _fetchedAt < MaxAgeMs
            && _programs.Count > 0) return;
        await RefreshAsync(channels).ConfigureAwait(false);
    }

    private sealed class Wanted
    {
        public readonly Dictionary<string, string> ByTvgId = new();
        public readonly Dictionary<string, string> ByName = new();
        public readonly Dictionary<string, string> ByFlatId = new();
        public readonly List<(HashSet<string> Toks, string Id)> TokenIndex = new();
        public readonly Dictionary<string, List<string>> FlatGroups = new();
        public readonly Dictionary<string, List<string>> NormGroups = new();
    }

    private static Wanted BuildWanted(List<Channel> channels)
    {
        var w = new Wanted();
        foreach (var ch in channels)
        {
            var tvg = (ch.TvgId ?? "").Trim();
            if (tvg.Length > 0)
            {
                w.ByTvgId.TryAdd(tvg, ch.Id);
                var b = BaseId(tvg);
                if (b.Length > 0) w.ByTvgId.TryAdd(b, ch.Id);
                var fl = FlatId(tvg);
                if (fl.Length > 0)
                {
                    w.ByFlatId.TryAdd(fl, ch.Id);
                    if (!w.FlatGroups.TryGetValue(fl, out var gl)) w.FlatGroups[fl] = gl = new List<string>();
                    gl.Add(ch.Id);
                }
            }
            var nk = NormalizeName(ch.Name ?? "");
            if (nk.Length > 0)
            {
                w.ByName.TryAdd(nk, ch.Id);
                if (!w.NormGroups.TryGetValue(nk, out var gn)) w.NormGroups[nk] = gn = new List<string>();
                gn.Add(ch.Id);
            }
            var toks = WordTokens(ch.Name ?? "");
            if (toks.Count > 0) w.TokenIndex.Add((toks, ch.Id));
        }
        return w;
    }

    private static List<string> SubsetMatchAll(Wanted wanted, string displayName)
    {
        var st = WordTokens(displayName);
        var outList = new List<string>();
        if (st.Count == 0) return outList;
        foreach (var (toks, id) in wanted.TokenIndex)
            if (SubsetHit(st, toks) || SubsetHit(toks, st)) outList.Add(id);
        return outList;
    }

    private static List<string> ResolveAll(Wanted wanted, string srcId, string displayName)
    {
        var ids = new LinkedHashSet<string>();
        if (wanted.ByTvgId.TryGetValue(srcId, out var a)) ids.Add(a);
        if (wanted.ByTvgId.TryGetValue(BaseId(srcId), out var b)) ids.Add(b);
        var fl = FlatId(srcId);
        if (fl.Length > 0)
        {
            if (wanted.ByFlatId.TryGetValue(fl, out var c)) ids.Add(c);
            if (wanted.FlatGroups.TryGetValue(fl, out var gl)) foreach (var x in gl) ids.Add(x);
            var flb = FlatId(BaseId(srcId));
            if (flb.Length > 0 && flb != fl)
            {
                if (wanted.ByFlatId.TryGetValue(flb, out var d)) ids.Add(d);
                if (wanted.FlatGroups.TryGetValue(flb, out var gl2)) foreach (var x in gl2) ids.Add(x);
            }
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var nn = NormalizeName(displayName);
            if (nn.Length > 0)
            {
                if (wanted.ByName.TryGetValue(nn, out var e)) ids.Add(e);
                if (wanted.NormGroups.TryGetValue(nn, out var gn)) foreach (var x in gn) ids.Add(x);
            }
            if (ids.Count == 0) foreach (var x in SubsetMatchAll(wanted, displayName)) ids.Add(x);
        }
        return ids.ToList();
    }

    private sealed class LinkedHashSet<T> : IEnumerable<T> where T : notnull
    {
        private readonly HashSet<T> _set = new();
        private readonly List<T> _order = new();
        public int Count => _order.Count;
        public void Add(T item) { if (_set.Add(item)) _order.Add(item); }
        public IEnumerator<T> GetEnumerator() => _order.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public List<T> ToList() => new(_order);
    }

    private async Task RefreshAsync(List<Channel> channels)
    {
        try
        {
            var wanted = BuildWanted(channels);
            if (wanted.ByTvgId.Count == 0 && wanted.ByName.Count == 0) return;
            var merged = new Dictionary<string, List<EpgProgramme>>();
            var total = 0;
            foreach (var url in Sources)
            {
                try
                {
                    total = await ParseSourceAsync(url, wanted, merged, total).ConfigureAwait(false);
                }
                catch { /* next source */ }
            }
            if (merged.Count > 0)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var trimmed = new Dictionary<string, List<EpgProgramme>>();
                foreach (var kv in merged)
                {
                    var list = kv.Value.Where(p => p.StopMs > now - WindowPastMs)
                        .OrderBy(p => p.StartMs).Take(MaxPerChannel).ToList();
                    if (list.Count > 0) trimmed[kv.Key] = list;
                }
                _programs = trimmed;
                _fetchedAt = now;
                SaveCache();
            }
        }
        catch { /* best effort */ }
    }

    private static async Task<int> ParseSourceAsync(
        string url, Wanted wanted,
        Dictionary<string, List<EpgProgramme>> @out, int total)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", StreamProber.DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is < 200 or > 399) return total;
#if NET8_0_OR_GREATER
        await using var raw = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffered = raw.CanSeek ? raw : new BufferedStream(raw, 64 * 1024);
        var probe = new byte[2];
        var read = await buffered.ReadAsync(probe.AsMemory(0, 2)).ConfigureAwait(false);
        Stream stream;
        if (read == 2 && probe[0] == 0x1f && probe[1] == 0x8b)
        {
            var prefix = new MemoryStream(probe, 0, read, writable: false);
            stream = new ConcatenatedStream(prefix, buffered);
            var gz = new GZipStream(stream, CompressionMode.Decompress);
            try { ParseXml(gz, wanted, @out, ref total); }
            finally { gz.Dispose(); }
            return total;
        }
        var all = new MemoryStream();
        if (read > 0) all.Write(probe, 0, read);
        await buffered.CopyToAsync(all).ConfigureAwait(false);
        all.Position = 0;
        ParseXml(all, wanted, @out, ref total);
        return total;
#else
        return total;
#endif
    }

    private sealed class ConcatenatedStream(Stream first, Stream second) : Stream
    {
        private Stream _cur = first;
        private readonly Stream _second = second;
        private bool _firstDone;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _cur.Read(buffer, offset, count);
            if (n == 0 && !_firstDone) { _firstDone = true; _cur = _second; n = _cur.Read(buffer, offset, count); }
            return n;
        }
        public override long Seek(long o, SeekOrigin org) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _cur.Dispose(); } catch { } try { _second.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    private static void ParseXml(
        Stream stream, Wanted wanted,
        Dictionary<string, List<EpgProgramme>> @out, ref int total)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var epgIdToOurs = new Dictionary<string, List<string>>();
        var settings = new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? "";
                var displayName = "";
                if (!reader.IsEmptyElement)
                {
                    var depth = reader.Depth;
                    while (reader.Read() && reader.Depth > depth)
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "display-name"
                            && displayName.Length == 0)
                            displayName = reader.ReadElementContentAsString().Trim();
                    }
                }
                var ours = ResolveAll(wanted, id, displayName);
                if (ours.Count > 0 && id.Length > 0) epgIdToOurs[id] = ours;
            }
            else if (reader.Name == "programme")
            {
                var epgCh = reader.GetAttribute("channel") ?? "";
                if (!epgIdToOurs.TryGetValue(epgCh, out var oursList)) { reader.Skip(); continue; }
                var start = ParseTime(reader.GetAttribute("start"));
                var stop = ParseTime(reader.GetAttribute("stop"));
                string title = "", desc = "";
                if (!reader.IsEmptyElement)
                {
                    var depth = reader.Depth;
                    while (reader.Read() && reader.Depth > depth)
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (reader.Name == "title" && title.Length == 0)
                            title = reader.ReadElementContentAsString().Trim();
                        else if (reader.Name == "desc" && desc.Length == 0)
                            desc = reader.ReadElementContentAsString().Trim();
                    }
                }
                else
                {
                    reader.Skip();
                }
                if (oursList.Count > 0 && title.Length > 0 && start > 0 && stop > start
                    && stop > now - WindowPastMs && start < now + WindowFutureMs)
                {
                    foreach (var ours in oursList)
                    {
                        if (!@out.TryGetValue(ours, out var list)) @out[ours] = list = new List<EpgProgramme>();
                        if (list.Count < MaxPerChannel && total < MaxTotal)
                        {
                            list.Add(new EpgProgramme
                            {
                                ChannelId = ours, StartMs = start, StopMs = stop,
                                Title = title, Desc = desc,
                            });
                            total++;
                        }
                    }
                }
            }
        }
    }

    private static long ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0L;
        try
        {
            var t = raw.Trim();
            string offset = "+0000";
            var m = Regex.Match(t, @"^(.*?)\s*([+-]\d{4}|Z)$");
            string core;
            if (m.Success)
            {
                core = m.Groups[1].Value;
                offset = m.Groups[2].Value == "Z" ? "+0000" : m.Groups[2].Value;
            }
            else
            {
                core = t;
            }
            var dt = DateTime.ParseExact(core, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None);
            var off = new TimeSpan(
                int.Parse(offset[1..3]), int.Parse(offset[3..5]), 0);
            if (offset[0] == '-') off = -off;
            return new DateTimeOffset(dt, off).ToUnixTimeMilliseconds();
        }
        catch { return 0L; }
    }

    private void SaveCache()
    {
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["fetchedAt"] = _fetchedAt,
                ["programs"] = _programs.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(p => new Dictionary<string, object>
                    {
                        ["s"] = p.StartMs, ["e"] = p.StopMs,
                        ["t"] = p.Title, ["d"] = p.Desc,
                    }).ToList()),
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(payload));
        }
        catch { }
    }

    private bool LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = doc.RootElement;
            _fetchedAt = root.GetProperty("fetchedAt").GetInt64();
            var rebuilt = new Dictionary<string, List<EpgProgramme>>();
            foreach (var ch in root.GetProperty("programs").EnumerateObject())
            {
                var list = new List<EpgProgramme>();
                foreach (var m in ch.Value.EnumerateArray())
                {
                    list.Add(new EpgProgramme
                    {
                        ChannelId = ch.Name,
                        StartMs = m.GetProperty("s").GetInt64(),
                        StopMs = m.GetProperty("e").GetInt64(),
                        Title = m.GetProperty("t").GetString() ?? "",
                        Desc = m.TryGetProperty("d", out var dd) ? dd.GetString() ?? "" : "",
                    });
                }
                list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                rebuilt[ch.Name] = list;
            }
            _programs = rebuilt;
            return true;
        }
        catch { return false; }
    }
}
