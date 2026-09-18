// Srv TV for Windows — M3U playlist parser.
// Logic port of the Android M3UParser, including the Java-hash-based
// channel IDs: IDs come out byte-identical to the Android app, so
// favorites exported from the TV apply here unchanged.
using SrvTv.Models;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SrvTv.Data;

public static class M3UParser
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> OriyaTvgIds = new()
    {
        "ArgusNews.in",
        "EkamraBharatOdia.in",
        "NandighoshaTV.in",
    };

    public static async Task<List<Channel>> ParseFromUrlAsync(
        string url,
        ChannelSource source = ChannelSource.FREE_IPTV,
        string? languageOverride = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseM3UContent(body, source, languageOverride);
        }
        catch
        {
            return new List<Channel>();
        }
    }

    public static List<Channel> ParseM3UContent(
        string content,
        ChannelSource source = ChannelSource.FREE_IPTV,
        string? languageOverride = null)
    {
        var channels = new List<Channel>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXTINF:", StringComparison.Ordinal)) continue;
            var info = ParseExtInf(line, languageOverride);
            var url = FindNextUrl(lines, i + 1);
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("#", StringComparison.Ordinal)) continue;
            url = url.Trim();
            channels.Add(new Channel
            {
                Id = GenerateId(url, info.Name),
                Name = string.IsNullOrWhiteSpace(info.Name) ? "Unknown Channel" : info.Name,
                Url = url,
                Logo = info.Logo,
                Category = info.Group,
                Country = info.Country,
                Language = info.Language,
                IsHD = info.IsHD,
                Source = source,
                TvgId = info.TvgId,
                TvgName = info.TvgName,
                StreamFormat = Channel.DetectFormat(url),
            });
        }
        return channels;
    }

    private sealed record ExtInfData(
        string Name, string Logo, string Group, string Country,
        string Language, bool IsHD, string TvgId, string TvgName);

    private static ExtInfData ParseExtInf(string line, string? languageOverride)
    {
        var tvgId = ExtractAttr(line, "tvg-id") ?? "";
        var tvgName = ExtractAttr(line, "tvg-name") ?? "";
        var logo = ExtractAttr(line, "tvg-logo") ?? ExtractAttr(line, "logo") ?? "";
        var group = ExtractAttr(line, "group-title") ?? "General";
        var tvgCountry = ExtractAttr(line, "tvg-country") ?? ExtractAttr(line, "country");
        var idCountry = ExtractCountryFromTvgId(tvgId);
        var country = tvgCountry ?? idCountry ?? "IN";
        var tvgLanguage = ExtractAttr(line, "tvg-language") ?? ExtractAttr(line, "language");
        var language = tvgLanguage ?? languageOverride ?? CountryToLanguage(country) ?? "";
        string fixedLanguage;
        var baseTvg = tvgId.Contains('@') ? tvgId[..tvgId.IndexOf('@')] : tvgId;
        if (string.IsNullOrWhiteSpace(tvgLanguage) && OriyaTvgIds.Contains(baseTvg))
            fixedLanguage = "odia";
        else
            fixedLanguage = language;
        var isHD = line.Contains("HD", StringComparison.OrdinalIgnoreCase)
            || logo.Contains("hd", StringComparison.OrdinalIgnoreCase);
        var name = line[(line.LastIndexOf(',') + 1)..].Trim();
        return new ExtInfData(name, logo, group, country, fixedLanguage, isHD, tvgId, tvgName);
    }

    private static string? ExtractCountryFromTvgId(string tvgId)
    {
        var m = Regex.Match(tvgId, @"\.([A-Za-z]{2})@");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? CountryToLanguage(string country)
    {
        return country.ToUpperInvariant() switch
        {
            "IN" => "hindi",
            "US" or "GB" or "CA" or "AU" or "NZ" or "IE" or "ZA" => "english",
            _ => null,
        };
    }

    private static string? ExtractAttr(string line, string attr)
    {
        foreach (var pattern in new[]
                 {
                     attr + "=\"([^\"]*)\"",
                     attr + "='([^']*)'",
                     attr + "=([^ ]*)",
                 })
        {
            var m = Regex.Match(line, pattern);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static string? FindNextUrl(string[] lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            return line;
        }
        return null;
    }

    // Java String.hashCode, bit-exact (unchecked 32-bit overflow), then
    // reinterpreted unsigned — identical IDs to the Android app.
    public static int JavaHash(string s)
    {
        unchecked
        {
            var h = 0;
            foreach (var c in s) h = 31 * h + c;
            return h;
        }
    }

    public static string GenerateId(string url, string name)
    {
        return $"{unchecked((uint)JavaHash(name))}_{unchecked((uint)JavaHash(url))}";
    }
}
