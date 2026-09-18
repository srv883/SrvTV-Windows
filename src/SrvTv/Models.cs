// Srv TV for Windows — data models.
// Field-for-field port of the Android app's Channel/Category/Playlist
// models so channel IDs (Java-hash based) and caches stay compatible.
namespace SrvTv.Models;

public enum ChannelSource
{
    FREE_IPTV,
    SONY_LIV,
    HOTSTAR,
    AIRTEL_XTREAM,
    JIO_TV,
    MX_PLAYER,
    YUPPTV,
    CUSTOM_M3U,
    LALIGA
}

public enum StreamFormat
{
    HLS,
    DASH,
    MP4,
    MPEG,
    RTMP,
    OTHER
}

public sealed class Channel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Logo { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Country { get; set; } = "IN";
    public string Language { get; set; } = "Hindi";
    public bool IsHD { get; set; }
    public bool IsFavorite { get; set; }
    public ChannelSource Source { get; set; } = ChannelSource.FREE_IPTV;
    public string TvgId { get; set; } = "";
    public string TvgName { get; set; } = "";
    public StreamFormat StreamFormat { get; set; } = StreamFormat.HLS;
    public bool IsWorking { get; set; } = true;
    public int ChannelNumber { get; set; }
    public long LastWatched { get; set; }

    public Channel With(
        string? id = null, string? name = null, string? url = null, string? logo = null,
        string? category = null, string? country = null, string? language = null,
        bool? isHD = null, bool? isFavorite = null, ChannelSource? source = null,
        string? tvgId = null, string? tvgName = null, StreamFormat? streamFormat = null,
        bool? isWorking = null, int? channelNumber = null, long? lastWatched = null)
    {
        return new Channel
        {
            Id = id ?? Id, Name = name ?? Name, Url = url ?? Url, Logo = logo ?? Logo,
            Category = category ?? Category, Country = country ?? Country,
            Language = language ?? Language, IsHD = isHD ?? IsHD,
            IsFavorite = isFavorite ?? IsFavorite, Source = source ?? Source,
            TvgId = tvgId ?? TvgId, TvgName = tvgName ?? TvgName,
            StreamFormat = streamFormat ?? StreamFormat,
            IsWorking = isWorking ?? IsWorking,
            ChannelNumber = channelNumber ?? ChannelNumber,
            LastWatched = lastWatched ?? LastWatched,
        };
    }

    public static StreamFormat DetectFormat(string url)
    {
        if (url.Contains(".m3u8") || url.Contains("hls")) return StreamFormat.HLS;
        if (url.Contains(".mpd") || url.Contains("dash")) return StreamFormat.DASH;
        if (url.Contains(".mp4")) return StreamFormat.MP4;
        if (url.Contains(".ts") || url.Contains("mpeg")) return StreamFormat.MPEG;
        if (url.Contains("rtmp")) return StreamFormat.RTMP;
        return StreamFormat.HLS;
    }
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public int ChannelCount { get; set; }
    public int SortOrder { get; set; }
}

public enum PlaylistType
{
    M3U,
    M3U_PLUS,
    XSPF,
    JSON,
    CUSTOM_URL
}

public sealed class Playlist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public PlaylistType Type { get; set; } = PlaylistType.M3U;
    public long LastUpdated { get; set; }
    public int ChannelCount { get; set; }
    public bool IsActive { get; set; } = true;
}
