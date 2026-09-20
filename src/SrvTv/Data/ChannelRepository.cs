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

    public Action<string>? ProgressChanged;
    public Action<bool>? BackgroundBusyChanged;

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

    private const int CacheVersion = 14;
    private const int MaxSeedFavorites = 60;
    private const int MinCategoryCount = 8;

    private static readonly Dictionary<string, string> NameOverrides = new()
    {
        ["http://5.188.159.128:8070/Cartoon_Network/index.m3u8"] = "Cartoon Network",
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
    }

    public List<Channel> Channels { get { lock (_dataLock) return _channels.ToList(); } }
    public List<Category> Categories => _categories;
    public List<Playlist> Playlists { get { lock (_dataLock) return _playlists.ToList(); } }
    public IReadOnlySet<string> Favorites => _favorites;

    private bool _playlistsReady;

    public async Task InitializeAsync()
    {
        EnsurePlaylistState();
        var cachedOk = LoadCachedChannels();
        if (!cachedOk) await RefreshAllPlaylistsAsync().ConfigureAwait(false);
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

    public async Task RefreshAllPlaylistsAsync(bool quiet = false, Action<int, int>? onProbe = null)
    {
        await Task.Run(async () =>
        {
            if (!quiet) SetProgress("Downloading playlists...");
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
            var rank = new Dictionary<string, int>();
            var allChannels = results
                .OrderBy(r => PlaylistRank(r.Item1))
                .SelectMany(r => r.Item2)
                .ToList();
            _ = rank;

            allChannels.AddRange(LoadBundledChannels());
            var filtered = Dedupe(FilterChannels(allChannels)).Select(WithNameOverrides).ToList();
            if (!quiet) SetProgress($"Testing {filtered.Count} streams...");
            var working = await StreamProber.ProbeAllAsync(filtered, (a, b) =>
            {
                if (!quiet) SetProgress($"Testing streams... {a}/{b}");
                onProbe?.Invoke(a, b);
            }).ConfigureAwait(false);

            var workingIds = working.Select(c => c.Id).ToHashSet();
            var workingUrls = working.Select(c => c.Url).ToHashSet();
            List<Channel> previous;
            lock (_dataLock) previous = _channels.ToList();
            var freshById = filtered.ToDictionary(c => c.Id, c => c);
            var keep = new List<Channel>();
            foreach (var id in _favorites.ToList())
            {
                if (workingIds.Contains(id)) continue;
                var cand = freshById.TryGetValue(id, out var f) ? f
                    : previous.FirstOrDefault(c => c.Id == id);
                if (cand == null || workingUrls.Contains(cand.Url)) continue;
                workingUrls.Add(cand.Url);
                keep.Add(cand.With(isWorking: false));
            }
            var final = working.Concat(keep).ToList();
            lock (_dataLock)
            {
                if (final.Count > 0 || _channels.Count == 0)
                {
                    _channels.Clear();
                    _channels.AddRange(final);
                }
            }
            UpdateFavoriteStatus();
            ApplyWatchTimes();
            BuildCategories();
            SeedDefaultFavorites();
            CacheChannels();
            await StoreFreshSigsAsync().ConfigureAwait(false);
            if (!quiet) SetProgress("");
        }).ConfigureAwait(false);
    }

    private List<Channel> LoadBundledChannels()
    {
        try
        {
            var asm = typeof(ChannelRepository).Assembly;
            using var s = asm.GetManifestResourceStream("odia-extra.m3u");
            if (s == null) return new List<Channel>();
            using var r = new StreamReader(s);
            return M3UParser.ParseM3UContent(r.ReadToEnd(), ChannelSource.FREE_IPTV, "odia");
        }
        catch { return new List<Channel>(); }
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
            lock (_dataLock)
            {
                _channels.RemoveAll(c => c.Source.ToString() == playlistId);
                _channels.AddRange(Dedupe(FilterChannels(fresh)).Select(WithNameOverrides));
            }
            UpdateFavoriteStatus();
            ApplyWatchTimes();
            BuildCategories();
            CacheChannels();
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
            .OrderBy(FavRank)
            .ThenBy(ch => FavRank(ch) == int.MaxValue ? ch.Category.ToLowerInvariant() : "")
            .ThenByDescending(ch => _watchTimes.TryGetValue(ch.Id, out var ts) ? ts : ch.LastWatched)
            .ThenBy(ch => ch.Name.ToLowerInvariant())
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
        var key = EpgRepository.NormalizeName(channel.Name);
        if (key.Length == 0) return new List<Channel>();
        var curRank = ResolutionRank(channel.Name);
        return Channels
            .Where(c => c.Id != channel.Id && c.Url != channel.Url
                && EpgRepository.NormalizeName(c.Name) == key)
            .GroupBy(c => c.Url).Select(g => g.First())
            .OrderBy(c =>
            {
                var r = ResolutionRank(c.Name);
                if (r < 0) r = curRank;
                var cc = curRank < 0 ? r : curRank;
                return r > cc ? 100 + r : cc - r;
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
            for (var i = 0; i < _channels.Count; i++)
                _channels[i] = _channels[i].With(isFavorite: _favorites.Contains(_channels[i].Id));
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

    private void CacheChannels()
    {
        List<Channel> snapshot;
        lock (_dataLock) snapshot = _channels.ToList();
        if (snapshot.Count == 0) return;
        try
        {
            _prefs.PutString("cached_channels", JsonSerializer.Serialize(snapshot, JsonOpts));
            _prefs.PutLong("cache_time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _prefs.PutInt("cache_version", CacheVersion);
            _prefs.Apply();
        }
        catch { }
    }

    private bool LoadCachedChannels()
    {
        var json = _prefs.GetString("cached_channels", "");
        if (string.IsNullOrEmpty(json)) return false;
        var cacheTime = _prefs.GetLong("cache_time", 0L);
        var fresh = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cacheTime <= 6L * 60 * 60 * 1000
            && _prefs.GetInt("cache_version", 0) == CacheVersion;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<Channel>>(json, JsonOpts) ?? new();
            if (loaded.Count > 0)
            {
                lock (_dataLock) _channels.AddRange(loaded.Select(WithNameOverrides));
                UpdateFavoriteStatus();
                ApplyWatchTimes();
                BuildCategories();
                SeedDefaultFavorites();
            }
            return fresh && loaded.Count > 0;
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
