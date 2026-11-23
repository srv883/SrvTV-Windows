// Srv TV for Windows — channel repository.
// Faithful port of the Android ChannelRepository: same playlists, filters,
// categories, favorites (+group ordering), watch recency, EPG-matching
// helpers, probe protection, and JSON cache keys. Channel IDs are
// byte-identical (Java hash), so favorites exported from the TV apply here.
using SrvTv.Models;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SrvTv.Data;

public sealed class ChannelRepository
{
    private readonly PrefsStore _prefs;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<Channel> _channels = new();
    private readonly object _dataLock = new();
    private List<Category> _categories = new();
    private readonly List<Playlist> _playlists = new();
    private readonly HashSet<string> _favorites = new();
    private readonly Dictionary<string, long> _watchTimes = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private readonly SemaphoreSlim _findGate = new(1, 1);
    private CancellationTokenSource? _renewCts;
    private CancellationTokenSource? _findCts;
    private Task? _renewTask;

    private readonly WorkingLinksStore _workingLinksStore;
    private Dictionary<string, WorkingLink> _workingLinks = new(StringComparer.Ordinal);
    private Dictionary<string, WorkingLink> _workingLinksByKey = new(StringComparer.Ordinal);
    private static readonly System.Text.RegularExpressions.Regex PanelTokenParam =
        new(@"([?&])token=[^&]*&?");

    public Action<string>? ProgressChanged;
    public Action<bool>? BackgroundBusyChanged;
    // tested, total, repaired — threaded through to the Settings/main window.
    public Action<int, int, int>? FindLinksProgress;
    public bool FindLinksRunning { get { lock (_dataLock) return _findRunning; } }
    private bool _findRunning;

    // Sourav edition: no seed (user curates), no Odia tab.
    private const string FavoriteSeed = "";
    private const bool ShowOdia = false;

    private static readonly List<Playlist> DefaultPlaylists = new()
    {
        new Playlist { Id = "iptv_org_eng", Name = "English Channels", Url = "https://iptv-org.github.io/iptv/languages/eng.m3u", Type = PlaylistType.M3U },
        new Playlist { Id = "iptv_org_hin", Name = "Hindi Channels", Url = "https://iptv-org.github.io/iptv/languages/hin.m3u", Type = PlaylistType.M3U },
        new Playlist { Id = "iptv_org_ori", Name = "Odia Channels", Url = "https://iptv-org.github.io/iptv/languages/ori.m3u", Type = PlaylistType.M3U },
        new Playlist { Id = "iptv_org_in", Name = "India Channels", Url = "https://iptv-org.github.io/iptv/countries/in.m3u", Type = PlaylistType.M3U },
        new Playlist { Id = "iptv_org_sports", Name = "Sports Channels", Url = "https://iptv-org.github.io/iptv/categories/sports.m3u", Type = PlaylistType.M3U },
        new Playlist { Id = "iptv_org_kids", Name = "Kids Channels", Url = "https://iptv-org.github.io/iptv/categories/kids.m3u", Type = PlaylistType.M3U },
    };

    private static readonly HashSet<string> PinnedPlaylistIds = new()
    {
        "iptv_org_eng", "iptv_org_hin", "iptv_org_ori", "iptv_org_in",
        "iptv_org_sports", "iptv_org_kids",
    };

    private static readonly HashSet<string> RemovedPlaylistIds = new()
    {
        "iptv_org_tam", "iptv_org_tel", "iptv_org_mal", "iptv_org_ben",
        "iptv_org_mar", "iptv_org_pan", "iptv_org_kan", "iptv_org_guj",
        "iptv_org_asm", "iptv_org_urd", "iptv_org_kok", "iptv_org_mai",
        "iptv_org_snd", "odia_extra",
    };

    private static readonly Dictionary<string, string?> PlaylistLanguages = new()
    {
        ["iptv_org_eng"] = "english",
        ["iptv_org_hin"] = "hindi",
        ["iptv_org_ori"] = "odia",
        ["iptv_org_in"] = null,
        ["iptv_org_sports"] = null,
        ["iptv_org_kids"] = null,
    };

    private const int CacheVersion = 18;
    // Verified live Setanta feed (Eurasia / Setanta 3). All public Setanta
    // URLs in iptv-org are dead, so every one is swapped to this route.
    private const string SetantaWorkingUrl = "http://85.117.61.84/setanta3/index.m3u8";
    private const int MaxSeedFavorites = 60;
    private const int MinCategoryCount = 8;
    private const int RecentLimit = 50;

    // IPTV panel (EasyISP24): the playlist needs a per-IP token that
    // /watch/list mints for ~1h (measured; the old 10h/6h assumptions let it
    // expire, after which every panel stream answered 403). The token rides
    // inside every stream URL, so cache it briefly and rewrite the URLs well
    // before it ages out.
    private const string PanelBase = "http://103.151.60.162:2122";
    private const long PanelTokenTtlMs = 30L * 60 * 1000;
    private const long PanelRenewIntervalMs = 20L * 60 * 1000;
    private const string PanelUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    private static readonly HttpClient PanelHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    // The S20 FE web server (IPTVApp/web, Tailscale only) sits on the home
    // network and can reach the panel even when this PC's carrier blocks it.
    // Panel streams are fetched through its /p proxy using a token the phone
    // mints, so this machine never talks to 103.151.60.162 directly.
    private const string PhoneProxyBase = "http://100.73.53.88:8787";
    private static readonly HttpClient PhoneHttp = new() { Timeout = TimeSpan.FromSeconds(12) };

    // The premium panel feeds the user watches most. Their tvg-ids are the
    // panel's own ids, stable across token/url churn, so both the favorites
    // seeding and the "top of Favorites" order match on them.
    private static readonly string[] PinnedPanelTvgIds =
    {
        "bz8n", "bbym", "b22h", "b1au", "bfxw", "bqj2", "b77z", "bi9k", "bob4",
    };

    private static readonly Dictionary<string, string> NameOverrides = new()
    {
        ["http://5.188.159.128:8070/Cartoon_Network/index.m3u8"] = "Cartoon Network",
    };

    // Route-level URL swaps (feed URL died, identical stream lives
    // elsewhere). Applied after id computation so favorites survive.
    // NOTE: worker-bouquet MP4s were tried and REJECTED (2026-10-01):
    // finite moov-at-end recordings, not live.
    private static readonly Dictionary<string, string> UrlOverrides = new()
    {
        ["http://103.185.24.134:3001/SonyTenSports1/index.m3u8"] =
            "http://103.151.60.162:2122/play/bz8n/index.m3u8?token=",
        ["http://103.151.60.162:2122/play/a01b/index.m3u8?hls"] =
            "http://103.151.60.162:2122/play/bbym/index.m3u8?token=",
        ["http://103.151.60.162:2122/play/a028/index.m3u8?hls"] =
            "http://103.151.60.162:2122/play/b1au/index.m3u8?token=",
        ["https://r2.amml.in/sony-ten-3-hd/live.m3u8"] =
            "http://103.151.60.162:2122/play/b22h/index.m3u8?token=",
        ["https://cloudplay-sonyliv.pages.dev/max2.m3u8"] =
            "http://107.167.16.138/sonymax2/index.m3u8?token=test",
        ["https://cloudplay-sonyliv.pages.dev/pal.m3u8"] =
            "http://59.103.38.46:8000/play/a0ac/index.m3u8",
        ["http://stream.mcquack.net/234/index.m3u8"] = SetantaWorkingUrl,
        ["http://stream.mcquack.net/235/index.m3u8"] = SetantaWorkingUrl,
        ["http://stream.mcquack.net/313/index.m3u8"] = SetantaWorkingUrl,
        ["http://stream.mcquack.net/390/index.m3u8"] = SetantaWorkingUrl,
        ["http://85.117.61.84/setanta2/index.m3u8"] = SetantaWorkingUrl,
        ["http://sewv654wfcsdwfi87fwvgbngh.siauliairsavlt.pw/iptv/8P7MBSH7RRPL53/20074/index.m3u8"] =
            SetantaWorkingUrl,
        // Dead premium public-list routes -> matching panel feeds. The
        // NameRoutes below refine Select 1 vs 2 where one URL is shared.
        ["https://vg162.adcdsd123321.shop/7.m3u8"] =
            "http://103.151.60.162:2122/play/bob4/index.m3u8?token=",
        ["http://116.90.120.149:8000/play/a11x/index.m3u8"] =
            "http://103.151.60.162:2122/play/b77z/index.m3u8?token=",
        ["http://tvsen7.aynascope.net/ssport2hd/index.m3u8"] =
            "http://103.151.60.162:2122/play/bi9k/index.m3u8?token=",
    };

    // Name-level routing: a feed refresh can repoint a channel to a new dead
    // URL, so pin the known-live panel feed by brand. Ids (and favorites)
    // stay untouched. Mirrors the Android/web PREFERRED_ROUTES tables.
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Url)[] NameRoutes =
    {
        (new System.Text.RegularExpressions.Regex(@"bein\s+sports?\s*1\b(?!\s*turk)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "http://103.151.60.162:2122/play/bob4/index.m3u8?token="),
        (new System.Text.RegularExpressions.Regex(@"star\s+sports?\s+select\s+1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "http://103.151.60.162:2122/play/b77z/index.m3u8?token="),
        (new System.Text.RegularExpressions.Regex(@"star\s+sports?\s+select\s+2",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "http://103.151.60.162:2122/play/bi9k/index.m3u8?token="),
    };

    // These feeds use different display names for the same Sony Ten channel.
    // Keep their panel entries in the failure plan so every platform can move
    // from a dead public-list URL to a matching panel mirror automatically.
    private static readonly Dictionary<string, string[]> MirrorAliases = new()
    {
        ["sonysportsten2"] = new[] { "sonysports2" },
        ["sonysports2"] = new[] { "sonysportsten2" },
        ["sonysportsten3hindi"] = new[] { "ten3" },
        ["ten3"] = new[] { "sonysportsten3hindi" },
        ["sonysportsten5"] = new[] { "ten5" },
        ["ten5"] = new[] { "sonysportsten5" },
        // Select 1/2 panel routes are 503 as of 2026-11-22. Keep Star Sports
        // 2 DL as an automatic but visibly named stand-in, never as a silent
        // replacement for a Select card.
        ["starsportsselect1"] = new[] { "starsports2dl" },
        ["starsportsselect2"] = new[] { "starsports2dl" },
    };

    private static readonly List<string> FavoriteCategoryOrder = new()
    {
        "sports", "kids", "animation", "documentary", "movies", "news",
    };

    private static readonly HashSet<string> JunkCategoryParts = new(StringComparer.Ordinal)
    {
        "Religious", "Legislative", "Shop", "Shopping", "Weather",
        "Undefined", "Spirituality", "Prayer", "Devotional", "Parliament",
        "Government", "Astrology", "Public",
    };

    private static readonly HashSet<string> KeepableCategoryParts = new(StringComparer.Ordinal)
    {
        "News", "Entertainment", "Sports", "General", "Kids", "Music",
        "Movies", "Documentary", "Animation", "Series", "Education",
        "Business", "Classic", "Lifestyle", "Culture", "Travel", "Comedy",
        "Family", "Outdoor", "Cooking", "Football", "Cricket", "Science",
        "Art", "Nature", "History", "Politics", "Food", "Health", "Tech",
        "Auto", "Gaming",
    };

    private static readonly string[] ReligiousKeywords =
    {
        "aastha", "sanskar", "sadhna", "shraddha", "ishwar", "bhakti",
        "devotional", "spiritual", "iskcon", "jagran", "aarti", "puja",
        "mantra", "darshan", "adhya", "mahavani", "sadguru", "satsang",
        "astrology", "astrologer", "jyotish", "kundli", "vaastu", "horoscope",
        "bible", "gospel", "quran", "islamic", "masjid", "temple", "kirpan",
        "gurdwara", "church", "preacher", "praise", "worship", "sermon",
        "ajtak", "madani", "faith", "dharm",
    };

    private static readonly string[] FootballKeywords =
    {
        "football", "soccer", "premier league", "epl", "laliga", "bundesliga",
        "serie a", "ligue 1", "champions league", "europa", "uefa", "fifa",
        "espn", "bein", "dazn", "sky sport", "supersport", "sports18", "sony ten",
        "star sports", "eleven sports", "nbc sport", "optus sport", "sport",
    };

    public ChannelRepository(PrefsStore prefs)
    {
        _prefs = prefs;
        _workingLinksStore = WorkingLinksStore.Default();
        _workingLinks = _workingLinksStore.Read();
        RebuildWorkingLinkIndex();
    }

    public List<Channel> Channels { get { lock (_dataLock) return _channels.ToList(); } }
    public List<Category> Categories => _categories;
    public List<Playlist> Playlists { get { lock (_dataLock) return _playlists.ToList(); } }
    public IReadOnlySet<string> Favorites => _favorites;

    private bool _playlistsReady;

    public async Task InitializeAsync()
    {
        await BootAsync(null).ConfigureAwait(false);
        StartPanelAutoRenew();
    }

    private void EnsurePlaylistState()
    {
        if (_playlistsReady) return;
        LoadFavorites();
        LoadWatchTimes();
        LoadPlaylists();
        if (_playlists.Count == 0)
        {
            _playlists.AddRange(DefaultPlaylists.Select(ClonePlaylist));
            SavePlaylists();
        }
        var missing = DefaultPlaylists.Where(d => _playlists.All(p => p.Id != d.Id)).ToList();
        if (missing.Count > 0)
        {
            _playlists.AddRange(missing.Select(ClonePlaylist));
            SavePlaylists();
        }
        if (_playlists.RemoveAll(p => RemovedPlaylistIds.Contains(p.Id)) > 0) SavePlaylists();
        _playlistsReady = true;
    }

    private static Playlist ClonePlaylist(Playlist p) => new()
    {
        Id = p.Id, Name = p.Name, Url = p.Url, Type = p.Type,
        LastUpdated = p.LastUpdated, ChannelCount = p.ChannelCount, IsActive = p.IsActive,
    };

    public async Task<bool> BootRecheckAsync()
    {
        SetBackgroundBusy(true);
        try
        {
            EnsurePlaylistState();
            lock (_dataLock) { if (_channels.Count == 0) return false; }
            if (!CacheFreshWithin(15) && await ManifestsChangedAsync().ConfigureAwait(false))
            {
                await RefreshAllPlaylistsAsync(quiet: true).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        catch { return false; }
        finally { SetBackgroundBusy(false); }
    }

    private bool CacheFreshWithin(int minutes)
    {
        try
        {
            var t = _prefs.GetLong("cache_time", 0L);
            return t > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t <= minutes * 60 * 1000L;
        }
        catch { return false; }
    }

    private static readonly HttpClient SigHttp = new();

    private async Task<bool> ManifestsChangedAsync()
    {
        var targets = _playlists.Where(p => PinnedPlaylistIds.Contains(p.Id)).ToList();
        if (targets.Count == 0) return false;
        var stored = LoadPlaylistSigs();
        foreach (var p in targets)
        {
            var sig = await FetchPlaylistSigAsync(p.Url).ConfigureAwait(false);
            if (sig == null) return true;
            if (!stored.TryGetValue(p.Id, out var old) || old != sig) return true;
        }
        return false;
    }

    private async Task StoreFreshSigsAsync()
    {
        try
        {
            var after = new Dictionary<string, string>();
            foreach (var p in _playlists.Where(p => PinnedPlaylistIds.Contains(p.Id)))
            {
                var sig = await FetchPlaylistSigAsync(p.Url).ConfigureAwait(false);
                if (sig != null) after[p.Id] = sig;
            }
            if (after.Count > 0) SavePlaylistSigs(after);
        }
        catch { }
    }

    private Dictionary<string, string> LoadPlaylistSigs()
    {
        try
        {
            var json = _prefs.GetString("playlist_sigs", "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new Dictionary<string, string>(); }
    }

    private void SavePlaylistSigs(Dictionary<string, string> sigs)
    {
        try { _prefs.PutString("playlist_sigs", JsonSerializer.Serialize(sigs)); _prefs.Apply(); }
        catch { }
    }

    private static async Task<string?> FetchPlaylistSigAsync(string url)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            head.Headers.TryAddWithoutValidation("User-Agent", StreamProber.DefaultUserAgent);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var resp = await SigHttp.SendAsync(head, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is >= 200 and <= 399)
            {
                var etag = resp.Headers.ETag?.ToString()?.Trim() ?? "";
                var len = resp.Content.Headers.ContentLength?.ToString() ?? "";
                if (etag.Length > 0 || len.Length > 0) return $"{etag}|{len}";
            }
        }
        catch { }
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.TryAddWithoutValidation("User-Agent", StreamProber.DefaultUserAgent);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var resp = await SigHttp.SendAsync(get, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is >= 200 and <= 399 || code == 206)
            {
                var etag = resp.Headers.ETag?.ToString()?.Trim() ?? "";
                if (etag.Length > 0) return $"{etag}|";
            }
        }
        catch { }
        return null;
    }

    public async Task BootAsync(Action<string>? progress = null)
    {
        EnsurePlaylistState();
        // Show the disk cache instantly when present; refresh decision below.
        var hadCache = TryLoadCache();
        if (hadCache && IsCacheFresh())
        {
            // Re-evaluate cached health flags on every launch. The probe is
            // background-only, so a slow source cannot delay first paint.
            _ = CompleteProbeAsync();
            return;
        }
        // Stale or empty: fetch + publish fast with NO probing (UI up in
        // seconds), then verify favorites first and the rest in background.
        progress?.Invoke("Downloading playlists...");
        var filtered = await FetchFilteredAsync().ConfigureAwait(false);
        if (filtered.Count == 0) return;
        PublishChannels(filtered);
        progress?.Invoke("");
        _ = CompleteProbeAsync();
    }

    private bool IsCacheFresh()
    {
        var t = _prefs.GetLong("cache_time", 0L);
        return t > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t <= 6L * 60 * 60 * 1000
            && _prefs.GetInt("cache_version", 0) == CacheVersion;
    }

    private async Task<List<Channel>> FetchFilteredAsync()
    {
        var fetches = _playlists
            .Where(p => p.IsActive || PinnedPlaylistIds.Contains(p.Id))
            .Select(async playlist =>
            {
                try
                {
                    PlaylistLanguages.TryGetValue(playlist.Id, out var lang);
                    var list = await M3UParser.ParseFromUrlAsync(playlist.Url, ChannelSource.FREE_IPTV, lang)
                        .ConfigureAwait(false);
                    return (playlist.Id, list);
                }
                catch { return (playlist.Id, new List<Channel>()); }
            }).ToList();
        var results = await Task.WhenAll(fetches).ConfigureAwait(false);
        var allChannels = results
            .OrderBy(r => PlaylistRank(r.Item1))
            .SelectMany(r => r.Item2)
            .ToList();
        allChannels.AddRange(LoadBundledChannels());
        allChannels.AddRange(await LoadPanelChannelsAsync().ConfigureAwait(false));
        // Learned links are applied last so they win over static routes; ids
        // (and favorites) stay exactly as the feed computed them.
        return Dedupe(FilterChannels(allChannels))
            .Select(ch => ApplyWorkingLink(WithOverrides(ch))).ToList();
    }

    private void PublishChannels(List<Channel> channels)
    {
        lock (_dataLock)
        {
            _channels.Clear();
            _channels.AddRange(channels);
        }
        EnsurePinnedSportsFavorites();
        UpdateFavoriteStatus();
        ApplyWatchTimes();
        BuildCategories();
        SeedDefaultFavorites();
        CacheChannels();
    }

    // Background verification: favorites first (what the user watches),
    // then everything else. Only flips health flags + rebuilds counts;
    // the visible lists are snapshots, so no UI marshaling is needed.
    public async Task CompleteProbeAsync()
    {
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return;
        SetBackgroundBusy(true);
        try
        {
            List<string> favIds;
            lock (_dataLock) favIds = _favorites.ToList();
            var favs = Channels.Where(c => favIds.Contains(c.Id)).ToList();
            Log.Write($"probe: verifying {favs.Count} favorites first...");
            ApplyWorkingFlags(await ProbeIdsAsync(favs).ConfigureAwait(false), favIds);
            CacheChannels(updateTimestamp: false);
            Log.Write("probe: favorites done; checking the rest in background...");
            List<Channel> rest;
            lock (_dataLock) rest = _channels.Where(c => !favIds.Contains(c.Id)).ToList();
            ApplyWorkingFlags(
                await ProbeIdsAsync(rest).ConfigureAwait(false),
                rest.Select(c => c.Id).ToList());
            BuildCategories();
            CacheChannels(updateTimestamp: false);
            Log.Write("probe: background check done.");
        }
        catch (Exception ex) { Log.Write("background probe failed: " + ex.Message); }
        finally
        {
            SetBackgroundBusy(false);
            _probeGate.Release();
        }
    }

    private static async Task<HashSet<string>> ProbeIdsAsync(List<Channel> channels)
    {
        if (channels.Count == 0) return new HashSet<string>();
        var ok = await StreamProber.ProbeAllAsync(channels).ConfigureAwait(false);
        return ok.Select(c => c.Id).ToHashSet();
    }

    private void ApplyWorkingFlags(HashSet<string> passed, List<string> ids)
    {
        lock (_dataLock)
        {
            foreach (var id in ids)
            {
                var idx = _channels.FindIndex(c => c.Id == id);
                if (idx >= 0) _channels[idx] = _channels[idx].With(isWorking: passed.Contains(id));
            }
        }
        UpdateFavoriteStatus();
    }

    public async Task RefreshAllPlaylistsAsync(bool quiet = false, Action<int, int>? onProbe = null)
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!quiet) SetProgress("Downloading playlists...");
            var filtered = await FetchFilteredAsync().ConfigureAwait(false);
            if (filtered.Count == 0)
            {
                if (!quiet) SetProgress("Refresh failed — keeping existing channels");
                return;
            }

            // Parsed manifests are usable immediately. Stream health probing
            // runs after publication so a dead CDN cannot block the grid.
            PublishChannels(filtered);
            _ = StoreFreshSigsAsync();
            _ = CompleteProbeAsync();
            if (!quiet) SetProgress("");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private List<Channel> LoadBundledChannels()
    {
        try
        {
            var asm = typeof(ChannelRepository).Assembly;
            var channels = new List<Channel>();
            foreach (var (resource, language) in new[]
            {
                ("odia-extra.m3u", "odia"),
                ("mirrors.m3u", (string?)null),
            })
            {
                using var s = asm.GetManifestResourceStream(resource);
                if (s == null) continue;
                using var r = new StreamReader(s);
                channels.AddRange(M3UParser.ParseM3UContent(
                    r.ReadToEnd(), ChannelSource.FREE_IPTV, language));
            }
            return channels;
        }
        catch { return new List<Channel>(); }
    }

    // Cached panel token, refreshed from /watch/list when it ages out. The
    // panel binds the token to the requesting IP, so each machine mints its
    // own instead of sharing a link that would 403 elsewhere.
    private async Task<string?> PanelTokenAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cached = _prefs.GetString("panel_token", "");
        var at = _prefs.GetLong("panel_token_at", 0L);
        if (!string.IsNullOrWhiteSpace(cached) && now - at is >= 0 and < PanelTokenTtlMs)
            return cached;
        var fresh = await FetchPanelTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fresh))
            return string.IsNullOrWhiteSpace(cached) ? null : cached;
        _prefs.PutString("panel_token", fresh);
        _prefs.PutLong("panel_token_at", now);
        _prefs.Apply();
        return fresh;
    }

    private static async Task<string?> FetchPanelTokenAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, PanelBase + "/watch/list");
            req.Headers.TryAddWithoutValidation("User-Agent", PanelUserAgent);
            using var resp = await PanelHttp.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    private async Task<List<Channel>> LoadPanelChannelsAsync()
    {
        var token = await PanelTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var first = await FetchPanelPlaylistAsync(token).ConfigureAwait(false);
            if (first.Count > 0) return WithPanelFallback(first, token);
            // A stale/blocked token answers 403; drop it and mint a fresh one.
            _prefs.PutString("panel_token", "");
            _prefs.PutLong("panel_token_at", 0L);
            _prefs.Apply();
            var retry = await PanelTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(retry))
            {
                var second = await FetchPanelPlaylistAsync(retry).ConfigureAwait(false);
                if (second.Count > 0) return WithPanelFallback(second, retry);
            }
        }
        // Direct panel unreachable (the carrier blocks its address here):
        // let the phone server on Tailscale proxy it from the home network.
        var viaPhone = await LoadPanelViaPhoneAsync().ConfigureAwait(false);
        if (viaPhone.Count > 0) return viaPhone;
        // Panel down / blocked: keep the last-known channel list so Sony
        // Sports / Star Sports never vanish from the library on a refresh.
        return LoadPanelFallback(null);
    }

    // The panel sometimes drops premium feeds (Sony Sports, Ten Cricket)
    // from its playlist while the /play endpoints still work. Keep the
    // shipped last-known line-up merged in: live entries win, the shipped
    // list fills the gaps so those channels (and their favorites) stay.
    private static List<Channel> WithPanelFallback(List<Channel> live, string? token)
    {
        var result = new List<Channel>(live);
        var seen = new HashSet<string>(
            live.Select(c => c.TvgId).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var ch in LoadPanelFallback(token))
        {
            if (!string.IsNullOrWhiteSpace(ch.TvgId) && !seen.Add(ch.TvgId)) continue;
            result.Add(ch);
        }
        return result;
    }

    // Mint a token through the phone proxy (so it is bound to the phone's
    // IP), then wrap each panel URL so the phone fetches the stream for us.
    private async Task<List<Channel>> LoadPanelViaPhoneAsync()
    {
        var token = await FetchPhonePanelTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) return new List<Channel>();
        _prefs.PutLong("panel_phone_token_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _prefs.Apply();
        // Ids stay derived from the original panel URLs, so favorites hold.
        return LoadPanelFallback(token)
            .Select(c => c.With(url: PhoneProxyBase + "/p?u=" + Uri.EscapeDataString(c.Url)))
            .ToList();
    }

    private static async Task<string?> FetchPhonePanelTokenAsync()
    {
        try
        {
            var target = PhoneProxyBase + "/p?u=" +
                Uri.EscapeDataString(PanelBase + "/watch/list");
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            req.Headers.TryAddWithoutValidation("User-Agent", PanelUserAgent);
            using var resp = await PhoneHttp.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    // Panel tokens age out after ~1h while this app stays open. Re-mint them
    // in the background and swap them inside the already-published URLs: the
    // channel ids stay untouched, so favorites and playback hold.
    public void StartPanelAutoRenew()
    {
        if (_renewTask != null) return;
        _renewCts = new CancellationTokenSource();
        _renewTask = Task.Run(() => PanelAutoRenewLoopAsync(_renewCts.Token));
    }

    private async Task PanelAutoRenewLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await EnsurePanelTokenFreshAsync().ConfigureAwait(false); } catch { }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    public async Task<bool> EnsurePanelTokenFreshAsync(bool force = false)
    {
        if (!await _renewGate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            bool hasDirect, hasPhone;
            lock (_dataLock)
            {
                hasDirect = _channels.Any(IsDirectPanelChannel);
                hasPhone = _channels.Any(IsPhonePanelChannel);
            }
            if (!hasDirect && !hasPhone) return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var changed = 0;

            // Direct panel URLs: mint on this machine and swap the token in
            // place. Ids stay untouched, so favorites and playback hold.
            if (hasDirect)
            {
                var at = _prefs.GetLong("panel_token_at", 0L);
                var due = force || at <= 0 || now - at < 0 || now - at >= PanelRenewIntervalMs;
                if (due)
                {
                    var token = await FetchPanelTokenAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        lock (_dataLock)
                        {
                            for (var i = 0; i < _channels.Count; i++)
                            {
                                var ch = _channels[i];
                                if (!IsDirectPanelChannel(ch)) continue;
                                var url = RenewDirectPanelUrl(ch.Url, token);
                                if (url == ch.Url) continue;
                                _channels[i] = ch.With(url: url);
                                changed++;
                            }
                        }
                        _prefs.PutString("panel_token", token);
                        _prefs.PutLong("panel_token_at", now);
                        _prefs.Apply();
                    }
                }
            }

            // Phone-proxied panel URLs (used when this PC cannot reach the
            // panel directly): only the phone can mint a token for those.
            if (hasPhone)
            {
                var at = _prefs.GetLong("panel_phone_token_at", 0L);
                var due = force || at <= 0 || now - at < 0 || now - at >= PanelRenewIntervalMs;
                if (due)
                {
                    var token = await FetchPhonePanelTokenAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        lock (_dataLock)
                        {
                            for (var i = 0; i < _channels.Count; i++)
                            {
                                var ch = _channels[i];
                                if (!IsPhonePanelChannel(ch)) continue;
                                var url = RenewPhonePanelUrl(ch.Url, token);
                                if (url == ch.Url) continue;
                                _channels[i] = ch.With(url: url);
                                changed++;
                            }
                        }
                        _prefs.PutLong("panel_phone_token_at", now);
                        _prefs.Apply();
                    }
                }
            }

            if (changed > 0)
            {
                CacheChannels(updateTimestamp: false);
                Log.Write($"panel token auto-renewed; rewrote {changed} urls");
            }
            return changed > 0;
        }
        catch (Exception ex) { Log.Write("panel auto-renew failed: " + ex.Message); return false; }
        finally { _renewGate.Release(); }
    }

    private static bool IsPhonePanelChannel(Channel ch)
    {
        var prefix = PhoneProxyBase + "/p?u=";
        if (!ch.Url.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            return Uri.UnescapeDataString(ch.Url[prefix.Length..])
                .Contains("/play/", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string RenewPhonePanelUrl(string url, string token)
    {
        var prefix = PhoneProxyBase + "/p?u=";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return url;
        string inner;
        try { inner = Uri.UnescapeDataString(url[prefix.Length..]); }
        catch { return url; }
        if (!inner.Contains("/play/", StringComparison.Ordinal)
            || !inner.Contains("token=", StringComparison.Ordinal)) return url;
        var renewed = System.Text.RegularExpressions.Regex.Replace(
            inner, "token=[^&\\s]*", "token=" + token);
        return prefix + Uri.EscapeDataString(renewed);
    }

    private static bool IsDirectPanelChannel(Channel ch)
        => ch.Url.StartsWith(PanelBase + "/play/", StringComparison.Ordinal);

    private static string RenewDirectPanelUrl(string url, string token)
    {
        if (!url.StartsWith(PanelBase + "/play/", StringComparison.Ordinal)) return url;
        if (!url.Contains("token=", StringComparison.Ordinal)) return url;
        return System.Text.RegularExpressions.Regex.Replace(
            url, "token=[^&\\s]*", "token=" + token);
    }

    // Last-known panel playlist shipped with the app. Used when the panel
    // is unreachable so premium channels stay listed (and can play again
    // as soon as the panel is back with a fresh token).
    private static List<Channel> LoadPanelFallback(string? token)
    {
        try
        {
            var asm = typeof(ChannelRepository).Assembly;
            using var s = asm.GetManifestResourceStream("panel.m3u");
            if (s == null) return new List<Channel>();
            using var r = new StreamReader(s);
            var text = r.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(token))
                text = System.Text.RegularExpressions.Regex.Replace(
                    text, "token=[^&\\s]*", "token=" + token);
            return M3UParser.ParseM3UContent(text, ChannelSource.FREE_IPTV, null);
        }
        catch { return new List<Channel>(); }
    }

    private static async Task<List<Channel>> FetchPanelPlaylistAsync(string token)
    {
        // all=1 returns the panel's full line-up (including premium feeds the
        // short "online" playlist omits); fall back to the short one if empty.
        foreach (var url in new[]
        {
            $"{PanelBase}/playlist.m3u8?hls&token={token}&all=1",
            $"{PanelBase}/playlist.m3u8?hls&token={token}",
        })
        {
            try
            {
                var list = await M3UParser.ParseFromUrlAsync(url).ConfigureAwait(false);
                if (list.Count > 0) return list;
            }
            catch { }
        }
        return new List<Channel>();
    }

    public async Task RefreshPlaylistAsync(string playlistId)
    {
        await Task.Run(async () =>
        {
            var playlist = _playlists.FirstOrDefault(p => p.Id == playlistId);
            if (playlist == null) return;
            PlaylistLanguages.TryGetValue(playlist.Id, out var lang);
            var fresh = await M3UParser.ParseFromUrlAsync(playlist.Url, ChannelSource.FREE_IPTV, lang)
                .ConfigureAwait(false);
            if (fresh.Count == 0) return;
            lock (_dataLock)
            {
                _channels.RemoveAll(c => c.Source.ToString() == playlistId);
                _channels.AddRange(Dedupe(FilterChannels(fresh))
                    .Select(ch => ApplyWorkingLink(WithOverrides(ch))));
            }
            UpdateFavoriteStatus();
            ApplyWatchTimes();
            BuildCategories();
            CacheChannels(updateTimestamp: true);
        }).ConfigureAwait(false);
    }

    private static int PlaylistRank(string id) => id switch
    {
        "iptv_org_eng" => 0,
        "iptv_org_hin" => 1,
        "iptv_org_ori" => 2,
        "iptv_org_in" => 11,
        "iptv_org_sports" => 12,
        "iptv_org_kids" => 13,
        _ => 100,
    };

    private static Channel WithNameOverrides(Channel ch)
    {
        return NameOverrides.TryGetValue(ch.Url, out var name) ? ch.With(name: name) : ch;
    }

    private static Channel WithUrlOverrides(Channel ch)
    {
        var url = FindUrlOverride(ch.Url);
        return url != null ? ch.With(url: url) : ch;
    }

    private static Channel WithOverrides(Channel ch)
    {
        if (NameOverrides.TryGetValue(ch.Url, out var name)) ch = ch.With(name: name);
        var url = FindUrlOverride(ch.Url);
        if (url != null)
            ch = ch.With(url: url,
                streamFormat: url.Contains("workers.dev") ? StreamFormat.MP4 : ch.StreamFormat);
        // Name routing runs last so it also covers refreshed URLs we have not
        // seen before. Never send Select 1 / Select 2 to Star Sports 2 DL.
        foreach (var (pattern, route) in NameRoutes)
        {
            if (!pattern.IsMatch(ch.Name) || ch.Url == route) continue;
            ch = ch.With(url: route, streamFormat: StreamFormat.HLS);
            break;
        }
        return ch;
    }

    private static string? FindUrlOverride(string sourceUrl)
    {
        if (UrlOverrides.TryGetValue(sourceUrl, out var replacement)) return replacement;
        // Cached panel URLs may already have a token. Match the stable path
        // after removing only that query parameter.
        var withoutToken = System.Text.RegularExpressions.Regex.Replace(
            sourceUrl, "([?&])token=[^&]*&?", "$1").TrimEnd('?', '&');
        return UrlOverrides.TryGetValue(withoutToken, out replacement) ? replacement : null;
    }

    // ------------------------------------------------------------------
    // On-demand "Find working links": verify every channel and, where its URL
    // is dead, persist the first mirror/panel route that positively answers.
    // Overrides are keyed by the stable id and stored token-free, so favorites,
    // watch history and the cache format are never touched.
    // ------------------------------------------------------------------

    private void RebuildWorkingLinkIndex()
    {
        _workingLinksByKey = _workingLinks.Values
            .Where(l => !string.IsNullOrWhiteSpace(l.NameKey))
            .GroupBy(l => l.NameKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static string StableUrl(string url)
        => PanelTokenParam.Replace(url ?? "", "$1").TrimEnd('?', '&');

    private static string WithPanelToken(string url, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return url;
        if (url.StartsWith(PanelBase + "/play/", StringComparison.Ordinal))
        {
            var param = new System.Text.RegularExpressions.Regex(@"([?&])token=[^&]*");
            return param.IsMatch(url)
                ? param.Replace(url, "${1}token=" + token)
                : url + (url.Contains('?') ? "&" : "?") + "token=" + token;
        }
        // Phone-proxied panel streams carry the token inside /p?u=<escaped url>.
        var prefix = PhoneProxyBase + "/p?u=";
        if (url.StartsWith(prefix, StringComparison.Ordinal))
        {
            try
            {
                var inner = Uri.UnescapeDataString(url[prefix.Length..]);
                if (!inner.Contains("/play/", StringComparison.Ordinal)) return url;
                inner = new System.Text.RegularExpressions.Regex(@"token=[^&\s]*").IsMatch(inner)
                    ? new System.Text.RegularExpressions.Regex(@"token=[^&\s]*").Replace(inner, "token=" + token)
                    : inner + (inner.Contains('?') ? "&" : "?") + "token=" + token;
                return prefix + Uri.EscapeDataString(inner);
            }
            catch { return url; }
        }
        return url;
    }

    private Channel ApplyWorkingLink(Channel ch)
    {
        var key = EpgRepository.NormalizeName(ch.Name);
        _workingLinks.TryGetValue(ch.Id, out var link);
        if (link == null) _workingLinksByKey.TryGetValue(key, out link);
        var url = link?.Url;
        if (string.IsNullOrWhiteSpace(url)) return ch;
        var authorized = WithPanelToken(url, _prefs.GetString("panel_token", ""));
        return authorized == ch.Url ? ch : ch.With(url: authorized);
    }

    public sealed class FindLinksResult
    {
        public int Tested;
        public int Total;
        public int Repaired;
        public int Dead;
    }

    public void CancelFindLinks()
    {
        try { _findCts?.Cancel(); } catch { }
    }

    public async Task<FindLinksResult> FindWorkingLinksAsync(CancellationToken ct = default)
    {
        var result = new FindLinksResult();
        if (!await _findGate.WaitAsync(0, ct).ConfigureAwait(false)) return result;
        lock (_dataLock) _findRunning = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        _findCts = linked;
        try
        {
            var snapshot = Channels;
            if (snapshot.Count == 0) return result;
            result.Total = snapshot.Count;
            var token = _prefs.GetString("panel_token", "");

            // Bundled mirrors and the panel lineup carry routes absent from the
            // public playlists (bo3x/bcfe, odia mirrors, Select duplicates).
            var extras = LoadBundledChannels();
            extras.AddRange(LoadPanelFallback(token));
            var byName = new Dictionary<string, List<Channel>>(StringComparer.Ordinal);
            var byTvg = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in snapshot.Select(ApplyWorkingLink).Concat(extras))
            {
                var key = EpgRepository.NormalizeName(ch.Name);
                if (key.Length > 0)
                {
                    if (!byName.TryGetValue(key, out var list)) byName[key] = list = new List<Channel>();
                    list.Add(ch);
                }
                if (!string.IsNullOrWhiteSpace(ch.TvgId))
                {
                    if (!byTvg.TryGetValue(ch.TvgId, out var list)) byTvg[ch.TvgId] = list = new List<Channel>();
                    list.Add(ch);
                }
            }
            var nameKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in snapshot)
                if (!nameKeys.ContainsKey(c.Id)) nameKeys[c.Id] = EpgRepository.NormalizeName(c.Name);

            List<Channel> CandidatesFor(Channel channel)
            {
                var found = new Dictionary<string, Channel>(StringComparer.Ordinal);
                found[StableUrl(channel.Url)] = channel;
                var key = EpgRepository.NormalizeName(channel.Name);
                var keys = new List<string>();
                if (key.Length > 0) keys.Add(key);
                if (MirrorAliases.TryGetValue(key, out var aliases)) keys.AddRange(aliases);
                var pool = new List<Channel>();
                foreach (var k in keys)
                    if (byName.TryGetValue(k, out var list)) pool.AddRange(list);
                if (!string.IsNullOrWhiteSpace(channel.TvgId) && byTvg.TryGetValue(channel.TvgId, out var tl))
                    pool.AddRange(tl);
                foreach (var (pattern, route) in NameRoutes)
                {
                    if (!pattern.IsMatch(channel.Name)) continue;
                    pool.Add(channel.With(url: route));
                    break;
                }
                // Panel routes first: they carry the channels this household
                // actually watches. OrderBy is stable in LINQ.
                var ordered = pool.OrderBy(c =>
                    c.Url.StartsWith(PanelBase + "/play/", StringComparison.Ordinal) ? 0 : 1);
                foreach (var cand in ordered)
                {
                    if (found.Count >= 6) break;
                    var url = WithPanelToken(cand.Url, token);
                    var stable = StableUrl(url);
                    if (!found.ContainsKey(stable)) found[stable] = cand.With(url: url);
                }
                return found.Values.ToList();
            }

            using var gate = new SemaphoreSlim(10);
            var counterLock = new object();
            var updates = new Dictionary<string, WorkingLink>(StringComparer.Ordinal);
            var gone = new HashSet<string>(StringComparer.Ordinal);
            var tasks = snapshot.Select(async original =>
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (linked.IsCancellationRequested) return;
                    var current = ApplyWorkingLink(original);
                    var health = await StreamProber.ProbeFastAsync(current.Url, current.StreamFormat)
                        .ConfigureAwait(false);
                    string? replacement = null;
                    if (health != StreamProber.ProbeResult.Working)
                    {
                        foreach (var cand in CandidatesFor(current))
                        {
                            if (StableUrl(cand.Url) == StableUrl(current.Url)) continue;
                            if (await StreamProber.ProbeFastAsync(cand.Url, cand.StreamFormat)
                                    .ConfigureAwait(false) == StreamProber.ProbeResult.Working)
                            {
                                replacement = cand.Url;
                                break;
                            }
                        }
                    }
                    lock (counterLock)
                    {
                        result.Tested++;
                        if (replacement != null)
                        {
                            result.Repaired++;
                            updates[current.Id] = new WorkingLink
                            {
                                Id = current.Id,
                                Name = current.Name,
                                NameKey = nameKeys[current.Id],
                                OriginalUrl = StableUrl(current.Url),
                                Url = StableUrl(replacement),
                                Reason = "current failed; alternate answered #EXTM3U",
                                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        }
                        else if (health == StreamProber.ProbeResult.Dead)
                        {
                            result.Dead++;
                            gone.Add(current.Id);
                        }
                        if (result.Tested % 25 == 0 || result.Tested == snapshot.Count)
                            FindLinksProgress?.Invoke(result.Tested, snapshot.Count, result.Repaired);
                    }
                }
                finally { gate.Release(); }
            }).ToList();

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Stop pressed or time limit */ }
            catch (Exception ex) { Log.Write("find links pass failed: " + ex.Message); }

            if (updates.Count > 0 || gone.Count > 0)
            {
                var previous = _workingLinks;
                var merged = new Dictionary<string, WorkingLink>(previous, StringComparer.Ordinal);
                foreach (var id in gone) merged.Remove(id);
                foreach (var kv in updates) merged[kv.Key] = kv.Value;
                _workingLinks = merged;
                RebuildWorkingLinkIndex();
                _workingLinksStore.Write(merged);

                lock (_dataLock)
                {
                    for (int i = 0; i < _channels.Count; i++)
                    {
                        var ch = _channels[i];
                        if (updates.TryGetValue(ch.Id, out var u))
                            _channels[i] = ch.With(url: WithPanelToken(u.Url, token), isWorking: true);
                        else if (gone.Contains(ch.Id) && previous.TryGetValue(ch.Id, out var old)
                                 && !string.IsNullOrWhiteSpace(old.OriginalUrl))
                            _channels[i] = ch.With(url: WithPanelToken(old.OriginalUrl, token));
                    }
                }
                BuildCategories();
                CacheChannels(updateTimestamp: false);
            }
        }
        finally
        {
            _findCts = null;
            lock (_dataLock) _findRunning = false;
            FindLinksProgress?.Invoke(result.Tested, result.Total, result.Repaired);
            _findGate.Release();
        }
        return result;
    }

    private static List<Channel> Dedupe(List<Channel> channels)
    {
        var seen = new HashSet<string>();
        return channels.Where(c => seen.Add(c.Url)).ToList();
    }

    private static List<Channel> FilterChannels(List<Channel> channels)
    {
        var dd = new System.Text.RegularExpressions.Regex("^DD\\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return channels.Where(ch =>
        {
            var name = ch.Name.Trim();
            var text = (name + " " + ch.Category).ToLowerInvariant();
            if (dd.IsMatch(name) || text.Contains("doordarshan")) return false;
            if (ReligiousKeywords.Any(text.Contains)) return false;
            var parts = ch.Category.Split(new[] { ';', ',' }, StringSplitOptions.None)
                .Select(p => p.Trim());
            if (parts.Any(JunkCategoryParts.Contains)) return false;
            return true;
        }).ToList();
    }

    private static bool IsKeepableCategory(string category)
    {
        var parts = category.Split(new[] { ';', ',' }, StringSplitOptions.None)
            .Select(p => p.Trim()).ToList();
        return parts.Count > 0
            && parts.All(KeepableCategoryParts.Contains)
            && parts.All(p => !JunkCategoryParts.Contains(p));
    }

    public List<Channel> GetChannelsByCategory(string category)
    {
        var snapshot = Channels;
        return category switch
        {
            "All" => snapshot,
            "India" => GetIndiaChannels(),
            "Odia" => GetOdiaChannels(),
            _ => snapshot.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    public List<Channel> GetChannelsForCategory(string id, string name)
    {
        var snapshot = Channels;
        return id switch
        {
            "fav" => GetFavoriteChannels(),
            "recent" => GetRecentChannels(),
            OfficialApps.CategoryId => OfficialApps.Cards.ToList(),
            "odia" => GetOdiaChannels(),
            "all" => snapshot,
            "india" => GetIndiaChannels(),
            "football" => GetFootballChannels(),
            _ => snapshot.Where(c => c.Category.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    public List<Channel> GetOdiaChannels()
    {
        if (!ShowOdia) return new List<Channel>();
        return Channels.Where(c =>
        {
            var lang = c.Language.ToLowerInvariant().Trim();
            return lang == "odia" || lang == "oriya";
        }).ToList();
    }

    public List<Channel> GetIndiaChannels()
    {
        var keep = new HashSet<string> { "english", "hindi", "odia", "oriya", "unknown", "" };
        return Channels.Where(ch =>
        {
            var text = (ch.Name + " " + ch.Category + " " + ch.TvgName).ToLowerInvariant();
            var lang = ch.Language.ToLowerInvariant().Trim();
            if (!keep.Contains(lang)) return false;
            var isIN = ch.Country.Equals("IN", StringComparison.OrdinalIgnoreCase);
            if (!isIN)
            {
                if (lang != "hindi" && lang != "odia" && lang != "oriya") return false;
                if (!IndianBrandKeywords.Any(k => BrandHit(text, k))) return false;
            }
            if (OtherLanguageKeywords.Any(text.Contains)) return false;
            var tokens = new HashSet<string>(RegexSplit(text));
            if (tokens.Any(ExtraReligiousTokens.Contains)) return false;
            return true;
        }).ToList();
    }

    private static IEnumerable<string> RegexSplit(string text)
    {
        return System.Text.RegularExpressions.Regex.Split(text, "[^a-z]+");
    }

    private static bool BrandHit(string text, string keyword)
    {
        var tokens = System.Text.RegularExpressions.Regex.Split(text, "[^a-z0-9&]+")
            .Where(t => t.Length > 0).ToList();
        var kw = System.Text.RegularExpressions.Regex.Split(keyword, "[^a-z0-9&]+")
            .Where(t => t.Length > 0).ToList();
        if (kw.Count == 0) return false;
        for (var i = 0; i + kw.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < kw.Count; j++)
                if (tokens[i + j] != kw[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    private static readonly string[] OtherLanguageKeywords =
    {
        "tamil", "telugu", "malayalam", "kannada", "bengali", "marathi",
        "punjabi", "gujarati", "assamese", "urdu", "sindhi", "konkani",
        "maithili", "bhojpuri", "tulu", "bangla", "nepali",
        "sun tv", "sun news", "sun music", "vijay tv", "vijay super",
        "raj tv", "raj music", "asianet", "mazhavil", "manorama",
        "mathrubhumi", "kaumudy", "flowers tv", "sakshi", "gemini movies",
        "maa tv", "zee tamil", "zee telugu", "zee kannada", "zee bangla",
        "zee keralam", "zee marathi", "zee punjabi", "zee biskope",
        "star maa", "star jalsha", "star vijay", "star pravah", "star suvarna",
        "sony marathi", "colors marathi", "colors kannada", "colors bangla",
        "colors gujarati", "surya tv", "surya movies", "surya music",
        "jaya tv", "polimer", "thanthi", "adithya", "v6 news",
        "tv9 gujarati", "tv9 telugu", "tv9 kannada", "tv9 marathi",
        "pitaara", "tashan", "ptc punjabi", "ptc news", "chardikla",
        "big ganga", "biskope", "prag news", "dy365", "rengoni",
        "majha", "ananda", "asmita", "amrita", "kairali", "udaya",
        "lokmat", "gemini", "zee yuva",
    };

    private static readonly HashSet<string> ExtraReligiousTokens = new()
    {
        "vedic", "kirtan", "bhajan", "gurbani", "astha", "sanskaar", "sadhana",
    };

    public List<Channel> GetFootballChannels()
    {
        return Channels.Where(ch =>
        {
            var t = (ch.Name + " " + ch.Category).ToLowerInvariant();
            return FootballKeywords.Any(t.Contains);
        }).ToList();
    }

    public List<Channel> GetFavoriteChannels()
    {
        var favs = Channels.Where(c => _favorites.Contains(c.Id)).ToList();
        return favs
            .OrderBy(ch => IsSports(ch) ? 0 : 1)
            .ThenBy(ch => IsSports(ch) ? 0 : FavRank(ch))
            .ThenBy(ch => IsSports(ch) ? "" : (FavRank(ch) == int.MaxValue ? ch.Category.ToLowerInvariant() : ""))
            .ThenByDescending(ch => _watchTimes.TryGetValue(ch.Id, out var ts) ? ts : ch.LastWatched)
            .ThenBy(ch => ch.Name.ToLowerInvariant())
            .ToList();
    }

    // Sports favorites lead the list, most recently watched first. Panel
    // feeds carry the panel's generic "Network" group, so match names too.
    private static readonly string[] SportsNameHints =
    {
        "sport", "spor ", "cricket", "football", "soccer", " ten ", "willow",
        "bein", "espn", "dazn", "eurosport", "wwe", " ufc ", " nba ", " nfl ", " f1 ", "fifa",
    };

    private static bool IsSports(Channel ch)
    {
        if (FavRank(ch) == 0) return true;
        var text = " " + (ch.Name + " " + ch.Category + " " + ch.TvgName).ToLowerInvariant() + " ";
        return SportsNameHints.Any(text.Contains);
    }

    // Most recently watched channels, newest first (the Recent tab).
    public List<Channel> GetRecentChannels()
    {
        var snapshot = Channels;
        List<KeyValuePair<string, long>> watched;
        lock (_dataLock) watched = _watchTimes.ToList();
        var byId = new Dictionary<string, Channel>();
        foreach (var c in snapshot) byId.TryAdd(c.Id, c);
        return watched
            .Where(kv => byId.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(RecentLimit)
            .Select(kv => byId[kv.Key])
            .ToList();
    }

    private static int FavRank(Channel ch)
    {
        var primary = ch.Category.Split(new[] { ';', ',' }, StringSplitOptions.None)
            .FirstOrDefault()?.Trim().ToLowerInvariant() ?? "";
        var i = FavoriteCategoryOrder.IndexOf(primary);
        return i >= 0 ? i : int.MaxValue;
    }

    public List<Channel> FindAlternates(Channel channel)
    {
        var current = ApplyWorkingLink(channel);
        var key = EpgRepository.NormalizeName(current.Name);
        if (key.Length == 0) return new List<Channel>();
        var relatedKeys = new HashSet<string>(StringComparer.Ordinal) { key };
        if (MirrorAliases.TryGetValue(key, out var aliases))
            foreach (var alias in aliases) relatedKeys.Add(alias);
        var curRank = ResolutionRank(current.Name);
        return Channels
            .Where(c => c.Id != current.Id && c.Url != current.Url
                && relatedKeys.Contains(EpgRepository.NormalizeName(c.Name)))
            .GroupBy(c => c.Url).Select(g => g.First())
            .OrderBy(c =>
            {
                var nameRank = EpgRepository.NormalizeName(c.Name) == key ? 0 : 1;
                var r = ResolutionRank(c.Name);
                if (r < 0) r = curRank;
                var cc = curRank < 0 ? r : curRank;
                return nameRank * 1000 + (r > cc ? 100 + r : cc - r);
            })
            .ThenBy(c => c.ChannelNumber)
            .ToList();
    }

    private static int ResolutionRank(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("2160") || System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b4k\b")) return 5;
        if (lower.Contains("1080")) return 4;
        if (lower.Contains("720")) return 3;
        if (lower.Contains("576") || lower.Contains("480")) return 2;
        if (lower.Contains("360")) return 1;
        return -1;
    }

    public void RecordWatch(string channelId)
    {
        lock (_dataLock)
        {
            var idx = _channels.FindIndex(c => c.Id == channelId);
            if (idx < 0) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _watchTimes[channelId] = now;
            _channels[idx] = _channels[idx].With(lastWatched: now);
        }
        SaveWatchTimes();
    }

    public List<Channel> SearchChannels(string query)
    {
        var snapshot = Channels;
        if (string.IsNullOrWhiteSpace(query)) return snapshot;
        var q = query.ToLowerInvariant();
        return snapshot.Where(c =>
            c.Name.ToLowerInvariant().Contains(q) ||
            c.Category.ToLowerInvariant().Contains(q) ||
            c.Language.ToLowerInvariant().Contains(q) ||
            c.Country.ToLowerInvariant().Contains(q)).ToList();
    }

    public void ToggleFavorite(string channelId)
    {
        if (!_favorites.Add(channelId)) _favorites.Remove(channelId);
        SaveFavorites();
        UpdateFavoriteStatus();
    }

    public bool IsFavorite(string channelId) => _favorites.Contains(channelId);

    public void AddPlaylist(Playlist p)
    {
        lock (_dataLock) _playlists.Add(p);
        SavePlaylists();
    }

    public void RemovePlaylist(string playlistId)
    {
        if (PinnedPlaylistIds.Contains(playlistId)) return;
        lock (_dataLock)
        {
            _playlists.RemoveAll(p => p.Id == playlistId);
            _channels.RemoveAll(c => c.Source.ToString() == playlistId);
        }
        SavePlaylists();
        BuildCategories();
    }

    // Seed the premium panel feeds into Favorites once. Relinking by name
    // hash keeps them across panel token/url rebuilds, and the one-time
    // marker lets the user remove one later without it coming back.
    private void EnsurePinnedSportsFavorites()
    {
        if (_prefs.GetString("pinned_sports_seeded", "") == "1") return;
        List<Channel> snapshot;
        lock (_dataLock) snapshot = _channels.ToList();
        var favNames = snapshot
            .Where(c => _favorites.Contains(c.Id))
            .Select(c => NormalizeName(c.Name))
            .ToHashSet();
        var added = 0;
        var found = 0;
        foreach (var tvg in PinnedPanelTvgIds)
        {
            var ch = snapshot.FirstOrDefault(c =>
                string.Equals(c.TvgId, tvg, StringComparison.OrdinalIgnoreCase));
            if (ch == null) continue;
            found++;
            if (_favorites.Contains(ch.Id)) continue;
            if (!favNames.Add(NormalizeName(ch.Name))) continue;
            _favorites.Add(ch.Id);
            added++;
        }
        if (found == 0) return;
        _prefs.PutString("pinned_sports_seeded", "1");
        _prefs.Apply();
        if (added > 0)
        {
            SaveFavorites();
            Log.Write($"pinned sports favorites seeded: +{added} (total {_favorites.Count})");
        }
    }

    private static string NormalizeName(string name) =>
        string.Join(" ", name.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void SeedDefaultFavorites()
    {
        try
        {
            var raw = FavoriteSeed.Trim();
            if (raw.Length == 0) return;
            var tokens = raw.Split('|').Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0).ToList();
            if (tokens.Count == 0) return;
            var seeded = LoadSeededIds().ToHashSet();
            List<Channel> snapshot;
            lock (_dataLock) snapshot = _channels.ToList();
            var added = 0;
            var touched = false;
            foreach (var ch in snapshot)
            {
                var lang = ch.Language.ToLowerInvariant().Trim();
                if (lang != "hindi" && lang != "english" && lang != "odia" && lang != "oriya") continue;
                var textLower = (ch.Name + " " + ch.Category + " " + ch.TvgName).ToLowerInvariant();
                if (OtherLanguageKeywords.Any(textLower.Contains)) continue;
                var matches = lang == "odia" || lang == "oriya" || tokens.Contains(lang) ||
                    tokens.Any(t => t == lang) || tokens.Any(t => BrandHit(textLower, t));
                if (!matches) continue;
                if (!seeded.Contains(ch.Id)) { seeded.Add(ch.Id); touched = true; }
                if (!_favorites.Contains(ch.Id) && added < MaxSeedFavorites)
                {
                    _favorites.Add(ch.Id);
                    added++;
                }
            }
            if (added > 0)
            {
                SaveFavorites();
                UpdateFavoriteStatus();
                BuildCategories();
            }
            if (touched || added > 0) SaveSeededIds(seeded);
            _prefs.PutString("favorites_seeded", "true");
            _prefs.Apply();
        }
        catch { }
    }

    private HashSet<string> LoadSeededIds()
    {
        try
        {
            var json = _prefs.GetString("seeded_ids", "");
            if (string.IsNullOrEmpty(json)) return new HashSet<string>();
            return new HashSet<string>(
                JsonSerializer.Deserialize<HashSet<string>>(json, JsonOpts) ?? new HashSet<string>());
        }
        catch { return new HashSet<string>(); }
    }

    private void SaveSeededIds(HashSet<string> ids)
    {
        try
        {
            _prefs.PutString("seeded_ids", JsonSerializer.Serialize(ids, JsonOpts));
            _prefs.Apply();
        }
        catch { }
    }

    private void BuildCategories()
    {
        List<Channel> snapshot;
        lock (_dataLock) snapshot = _channels.ToList();
        var counts = new Dictionary<string, int>();
        foreach (var ch in snapshot)
            counts[ch.Category] = counts.TryGetValue(ch.Category, out var n) ? n + 1 : 1;

        var result = new List<Category>
        {
            new() { Id = "fav", Name = "Favorites", ChannelCount = _favorites.Count, SortOrder = -3 },
            new() { Id = "recent", Name = "Recent Channels", ChannelCount = GetRecentChannels().Count, SortOrder = -2 },
            new() { Id = "india", Name = "India", ChannelCount = GetIndiaChannels().Count, SortOrder = -1 },
            new() { Id = "all", Name = "All Channels", ChannelCount = snapshot.Count, SortOrder = 0 },
            new() { Id = "football", Name = "Football", ChannelCount = GetFootballChannels().Count, SortOrder = 1 },
        };

        lock (_dataLock)
        {
            for (var i = 0; i < snapshot.Count && i < _channels.Count; i++)
            {
                if (_channels[i].Id == snapshot[i].Id && snapshot[i].ChannelNumber != i + 1)
                    _channels[i] = snapshot[i].With(channelNumber: i + 1);
            }
        }

        var idx = 0;
        foreach (var kv in counts
                     .Where(e => IsKeepableCategory(e.Key) && e.Value >= MinCategoryCount)
                     .OrderBy(e => e.Key))
        {
            result.Add(new Category
            {
                Id = kv.Key.ToLowerInvariant().Replace(" ", "_"),
                Name = kv.Key,
                ChannelCount = kv.Value,
                SortOrder = idx + 2,
            });
            idx++;
        }
        _categories = result;
    }

    private void UpdateFavoriteStatus()
    {
        lock (_dataLock)
        {
            RelinkFavoriteIds();
            for (var i = 0; i < _channels.Count; i++)
                _channels[i] = _channels[i].With(isFavorite: _favorites.Contains(_channels[i].Id));
        }
    }

    // Channel ids are "{nameHash}_{urlHash}". When a playlist is rebuilt the
    // url hash changes (tokens, mirrors), which used to silently drop saved
    // favorites. Re-point any orphaned favorite at the channel that still has
    // the same name hash so the Favorites tab keeps every saved channel.
    private void RelinkFavoriteIds()
    {
        if (_favorites.Count == 0 || _channels.Count == 0) return;
        var current = new HashSet<string>(_channels.Count);
        var byName = new Dictionary<string, List<string>>(_channels.Count);
        foreach (var c in _channels)
        {
            current.Add(c.Id);
            var sep = c.Id.IndexOf('_');
            if (sep <= 0) continue;
            var key = c.Id[..sep];
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = new List<string>();
            list.Add(c.Id);
        }
        var used = new HashSet<string>();
        foreach (var fav in _favorites)
            if (current.Contains(fav)) used.Add(fav);
        var stale = new List<string>();
        foreach (var fav in _favorites)
            if (!current.Contains(fav)) stale.Add(fav);
        if (stale.Count == 0) return;
        var add = new List<string>();
        foreach (var fav in stale)
        {
            var sep = fav.IndexOf('_');
            if (sep <= 0 || !byName.TryGetValue(fav[..sep], out var pool) || pool.Count == 0) continue;
            // Prefer a channel not already claimed by another favorite so
            // duplicate-named channels (mirrors/panel copies) stay distinct.
            var target = pool.FirstOrDefault(id => !used.Contains(id)) ?? pool[0];
            _favorites.Remove(fav);
            add.Add(target);
            used.Add(target);
        }
        foreach (var id in add) _favorites.Add(id);
        if (add.Count > 0)
        {
            SaveFavorites();
            Log.Write($"favorites relinked by name-hash: {add.Count} (total {_favorites.Count})");
        }
    }

    public void RefreshFavorites()
    {
        UpdateFavoriteStatus();
        BuildCategories();
    }

    public Channel? GetChannelById(string id)
    {
        lock (_dataLock) return _channels.FirstOrDefault(c => c.Id == id);
    }

    private void LoadFavorites()
    {
        try
        {
            var json = _prefs.GetString("favorites", "[]");
            var loaded = JsonSerializer.Deserialize<HashSet<string>>(json, JsonOpts);
            if (loaded != null) foreach (var id in loaded) _favorites.Add(id);
        }
        catch { }
    }

    private void SaveFavorites()
    {
        try
        {
            _prefs.PutString("favorites", JsonSerializer.Serialize(_favorites, JsonOpts));
            _prefs.Apply();
        }
        catch { }
    }

    private void LoadWatchTimes()
    {
        try
        {
            var json = _prefs.GetString("watch_times", "");
            if (string.IsNullOrEmpty(json)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOpts);
            if (loaded != null) foreach (var kv in loaded) _watchTimes[kv.Key] = kv.Value;
        }
        catch { }
    }

    private void ApplyWatchTimes()
    {
        lock (_dataLock)
        {
            foreach (var (id, ts) in _watchTimes)
            {
                var idx = _channels.FindIndex(c => c.Id == id);
                if (idx >= 0) _channels[idx] = _channels[idx].With(lastWatched: ts);
            }
        }
    }

    private void SaveWatchTimes()
    {
        try
        {
            Dictionary<string, long> snap;
            lock (_dataLock) snap = new Dictionary<string, long>(_watchTimes);
            _prefs.PutString("watch_times", JsonSerializer.Serialize(snap, JsonOpts));
            _prefs.Apply();
        }
        catch { }
    }

    private void LoadPlaylists()
    {
        try
        {
            var json = _prefs.GetString("playlists", "");
            if (string.IsNullOrEmpty(json)) return;
            var loaded = JsonSerializer.Deserialize<List<Playlist>>(json, JsonOpts);
            if (loaded != null) { lock (_dataLock) _playlists.AddRange(loaded); }
        }
        catch { }
    }

    private void SavePlaylists()
    {
        try
        {
            List<Playlist> snap;
            lock (_dataLock) snap = _playlists.ToList();
            _prefs.PutString("playlists", JsonSerializer.Serialize(snap, JsonOpts));
            _prefs.Apply();
        }
        catch { }
    }

    private void CacheChannels(bool updateTimestamp = true)
    {
        List<Channel> snapshot;
        lock (_dataLock) snapshot = _channels.ToList();
        if (snapshot.Count == 0) return;
        try
        {
            _prefs.PutString("cached_channels", JsonSerializer.Serialize(snapshot, JsonOpts));
            if (updateTimestamp)
                _prefs.PutLong("cache_time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _prefs.PutInt("cache_version", CacheVersion);
            _prefs.Apply();
        }
        catch { }
    }

    private bool TryLoadCache()
    {
        var json = _prefs.GetString("cached_channels", "");
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<Channel>>(json, JsonOpts) ?? new();
            if (loaded.Count > 0)
            {
                lock (_dataLock) _channels.AddRange(
                    loaded.Select(ch => ApplyWorkingLink(WithOverrides(ch))));
                EnsurePinnedSportsFavorites();
                UpdateFavoriteStatus();
                ApplyWatchTimes();
                BuildCategories();
                SeedDefaultFavorites();
            }
            return loaded.Count > 0;
        }
        catch { return false; }
    }

    private void SetProgress(string message) => ProgressChanged?.Invoke(message);
    private void SetBackgroundBusy(bool busy) => BackgroundBusyChanged?.Invoke(busy);

    private static readonly string[] IndianBrandKeywords =
    {
        "star sports", "star gold", "star maa", "star jalsha", "star vijay",
        "star pravah", "star suvarna", "star plus", "star movies", "star world",
        "star bharat", "star utsav", "star uthar", "star cinema", "jalsha",
        "vijay tv", "vijay super",
        "sony sab", "sony max", "sony pal", "sony marathi", "sony five",
        "sony ten", "sony sports", "sony wah", "sony entertainment", "sony liv",
        "sab tv",
        "zee tv", "zee cinema", "zee anmol", "zee marathi", "zee telugu",
        "zee kannada", "zee bangla", "zee news", "zee keralam", "zee tamil",
        "zee punjabi", "zee salwa", "zee yuva", "zee smile", "zee zindagi",
        "zing", "&tv", "&pictures", "&flix", "&prive",
        "colors", "rishtey", "colors marathi", "colors kannada", "colors bangla",
        "colors gujarati", "colors super", "colors cine", "colors infinity",
        "ndtv", "aaj tak", "india today", "times now", "times network",
        "republic", "news18", "abp", "india news", "tv9", "et now", "moneycontrol",
        "cnbc awaaz", "kanak", "news nation", "zee 24",
        "india tv", "surya tv", "surya movies", "surya music",
        "sun tv", "sun news", "sun music", "adithya", "kiran", "sakshi",
        "etv", "gemini", "maa tv", "maa gold", "raj tv", "raj music", "raj news",
        "jaya", "polimer", "thanthi", "makkal", "v6 news",
        "asianet", "mazhavil", "manorama", "mathrubhumi", "kaumudy", "flowers tv",
        "jaihind", "ten sports", "sports18", "shemaroo", "flame tv", "disha",
        "punjabi", "jalandhar", "chandigarh", "jagbani",
        "gurjari", "vanita", "rang punjab", "bhojpuri",
    };
}
