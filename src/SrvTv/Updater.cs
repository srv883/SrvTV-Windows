using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SrvTv;

// Self-update from GitHub Releases. installer/publish-release.ps1 uploads
// SrvTV-Setup.exe plus SrvTV-Setup.exe.sha256 to a release tagged vX.Y.Z;
// installed copies check the latest release, download it in the
// background, verify the hash, and run the setup quietly when the user
// says "Update now" or closes the app. Files the app downloads itself are
// not marked as "from the internet", so Windows SmartScreen never prompts.
internal static class Updater
{
    public const string Repo = "srv883/SrvTV-Windows";
    private const string SetupAsset = "SrvTV-Setup.exe";

    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    // Ready-to-run setup for a newer version, once downloaded and verified.
    public static string? PendingSetup { get; private set; }
    public static Version? PendingVersion { get; private set; }
    public static string PendingNotes { get; private set; } = "";

    public static event Action? UpdateReady;

    private static readonly HttpClient Http = CreateClient();
    private static int _checking;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SrvTV-Updater/" + CurrentText);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv", "updates");

    // Only copies installed by SrvTV-Setup.exe update themselves (not dev
    // builds or UI-test copies).
    public static bool Enabled =>
        TestApi != null || (!UiTest.On && File.Exists(Path.Combine(AppContext.BaseDirectory, "Uninstall Srv TV.exe")));

    // Dev aid: point a UI-test copy at a local fake of the releases API.
    private static string? TestApi => UiTest.On ? Environment.GetEnvironmentVariable("SRVTV_UPDATE_API") : null;
    private static string LatestApi => TestApi ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    public static void StartBackgroundChecks()
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            // Soon after start, so the "update required" popup shows while the
            // app is opening rather than in the middle of watching.
            await Task.Delay(TimeSpan.FromSeconds(TestApi != null ? 3 : 8)).ConfigureAwait(false);
            while (true)
            {
                try { await CheckAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("update check failed: " + ex.Message); }
                await Task.Delay(TimeSpan.FromHours(6)).ConfigureAwait(false);
            }
        });
    }

    // Returns a short status line for the Settings window.
    public static async Task<string> CheckAsync()
    {
        if (PendingVersion != null) return $"Version {PendingVersion.ToString(3)} is ready to install.";
        if (Interlocked.Exchange(ref _checking, 1) == 1) return "Already checking...";
        try
        {
            var json = await Http.GetStringAsync(LatestApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return "Couldn't read the latest version.";
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            var mine = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            if (latest <= mine) return $"Srv TV {CurrentText} is up to date.";

            string? setupUrl = null, hashUrl = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == SetupAsset) { setupUrl = url; size = a.GetProperty("size").GetInt64(); }
                else if (name == SetupAsset + ".sha256") hashUrl = url;
            }
            if (setupUrl == null || hashUrl == null) return $"Version {latest.ToString(3)} is out but its files are missing.";

            Log.Write($"update: {mine} -> {latest}, downloading");
            var expected = (await Http.GetStringAsync(hashUrl).ConfigureAwait(false))
                .Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            Directory.CreateDirectory(UpdateDir);
            var final = Path.Combine(UpdateDir, $"SrvTV-Setup-{latest.ToString(3)}.exe");
            if (!File.Exists(final) || Sha256(final) != expected)
            {
                var part = final + ".part";
                using (var resp = await Http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var dst = File.Create(part);
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
                if (size > 0 && new FileInfo(part).Length != size) { File.Delete(part); return "Download was incomplete; will retry later."; }
                if (Sha256(part) != expected) { File.Delete(part); Log.Write("update: hash mismatch"); return "Download failed its safety check; will retry later."; }
                File.Move(part, final, overwrite: true);
            }
            foreach (var old in Directory.GetFiles(UpdateDir, "SrvTV-Setup-*.exe"))
                if (!string.Equals(old, final, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch { } }

            PendingSetup = final;
            PendingVersion = latest;
            PendingNotes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                ? body.GetString() ?? "" : "";
            Log.Write($"update: {latest} ready");
            UpdateReady?.Invoke();
            return $"Version {latest.ToString(3)} is ready to install.";
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "No releases published yet."
                : "Couldn't reach GitHub. Check the internet connection.";
        }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // Run the downloaded setup quietly. relaunch: open Srv TV again afterwards.
    // The setup closes this app itself before replacing files.
    public static bool Apply(bool relaunch)
    {
        if (PendingSetup == null || !File.Exists(PendingSetup)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(PendingSetup, relaunch ? "/quiet /launch" : "/quiet")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            Log.Write("update: setup started" + (relaunch ? " (relaunch)" : ""));
            PendingSetup = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update: could not start setup: " + ex.Message);
            return false;
        }
    }
}
