using SrvTv.Models;

namespace SrvTv.Data;

// Shortcut cards to the official streaming services for channels the free
// feeds don't carry reliably. They are not streams: opening one launches
// the service's own site in the default browser (the user's subscription
// and login live there). Kept out of the channel list, cache and search.
public static class OfficialApps
{
    public const string CategoryId = "apps";
    public const string CategoryName = "Official Apps";
    private const string IdPrefix = "app:";

    public static readonly List<Channel> Cards = new()
    {
        Card("jiohotstar", "JioHotstar", "Star Sports • live cricket", "https://www.hotstar.com/in/sports", ChannelSource.HOTSTAR),
        Card("sonyliv", "Sony LIV", "Sony Sports Ten 1-5", "https://www.sonyliv.com/", ChannelSource.SONY_LIV),
        Card("jiotv", "JioTV", "Sony Ten, Eurosport, live TV", "https://www.jiotv.com/", ChannelSource.JIO_TV),
        Card("fancode", "FanCode", "Cricket and football tours", "https://www.fancode.com/", ChannelSource.CUSTOM_M3U),
        Card("willow", "Willow", "Cricket (US/Canada)", "https://www.willow.tv/", ChannelSource.CUSTOM_M3U),
    };

    public static bool IsShortcut(Channel ch) => ch.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static void Open(Channel ch)
    {
        if (!IsShortcut(ch)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ch.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Log.Write($"official app open failed: {ch.Url} {e.Message}");
        }
    }

    private static Channel Card(string id, string name, string blurb, string url, ChannelSource source) => new()
    {
        Id = IdPrefix + id,
        Name = name,
        Url = url,
        Category = blurb,
        Language = "",
        Source = source,
        StreamFormat = StreamFormat.OTHER,
    };
}
