// Srv TV for Windows — stream health prober.
// Port of the Android StreamProber: bounded parallelism, short timeouts,
// HTTP 200 + #EXTM3U-prefix check for HLS. Runs on thread-pool threads.
using SrvTv.Models;
using System.Net.Http;

namespace SrvTv.Data;

public static class StreamProber
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    private const int MaxParallelProbes = 24;
    private const int ProgressEvery = 25;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromMilliseconds(3000),
    };

    public static async Task<List<Channel>> ProbeAllAsync(
        List<Channel> channels,
        Action<int, int>? onProgress = null)
    {
        var total = channels.Count;
        if (total == 0) return new List<Channel>();
        using var gate = new SemaphoreSlim(MaxParallelProbes);
        var result = new List<Channel>();
        var tested = 0;
        var gateLock = new object();

        var tasks = channels.Select(async channel =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return (channel, ok: await ProbeAsync(channel).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var t in tasks)
        {
            var (channel, ok) = await t.ConfigureAwait(false);
            int done;
            lock (gateLock)
            {
                tested++;
                done = tested;
                if (ok) result.Add(channel.With(isWorking: true));
            }
            if (done % ProgressEvery == 0 || done == total)
                onProgress?.Invoke(done, total);
        }
        return result;
    }

    private static async Task<bool> ProbeAsync(Channel channel)
    {
        try
        {
            return await ProbeUrlAsync(channel.Url, channel.StreamFormat).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeUrlAsync(string url, StreamFormat format)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is < 200 or > 399) return false;
var prefix = new byte[9];
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var read = 0;
            while (read < prefix.Length)
            {
                var n = await stream.ReadAsync(prefix.AsMemory(read), cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return false;
            if (format == StreamFormat.HLS)
            {
                var text = System.Text.Encoding.UTF8.GetString(prefix, 0, read)
                    .Trim('\uFEFF', ' ', '\t', '\n', '\r');
                return text.StartsWith("#EXTM3U", StringComparison.Ordinal);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
