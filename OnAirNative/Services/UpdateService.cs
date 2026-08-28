using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OnAirNative.Services;

/// <summary>Result of checking GitHub Releases for a newer onAIr build.</summary>
public sealed record UpdateCheckResult(
    bool    UpdateAvailable,
    string  LatestVersion,
    string  ReleaseUrl,
    string? DownloadUrl,
    string? AssetName);

/// <summary>
/// Checks GitHub Releases for a newer onAIr version and can download +
/// launch the NSIS installer for a one-click update.
///
/// The repo is public, so the Releases API needs no auth token — just a
/// User-Agent header (GitHub rejects anonymous requests without one). The
/// unauthenticated rate limit is 60 requests/hour/IP, comfortably enough for
/// one check per app launch plus manual re-checks from the About tab.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/souz4rafael/onair-native/releases/latest";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("onAIr-Native", null));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Queries the latest GitHub release and compares it against <paramref name="currentVersion"/>.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion)
    {
        using var resp = await _http.GetAsync(LatestReleaseUrl);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var tagName       = root.GetProperty("tag_name").GetString() ?? "";
        var latestVersion = tagName.TrimStart('v', 'V');
        var releaseUrl    = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        string? downloadUrl = null;
        string? assetName   = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                assetName   = name;
                break;
            }
        }

        return new UpdateCheckResult(
            UpdateAvailable: IsNewer(currentVersion, latestVersion),
            LatestVersion:   latestVersion,
            ReleaseUrl:      releaseUrl,
            DownloadUrl:     downloadUrl,
            AssetName:       assetName);
    }

    /// <summary>Downloads the installer to a temp file, reporting 0.0–1.0 progress, and returns its path.</summary>
    public async Task<string> DownloadInstallerAsync(string downloadUrl, string assetName, IProgress<double>? progress = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), assetName);

        using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var totalBytes = resp.Content.Headers.ContentLength ?? -1L;

        await using var httpStream = await resp.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(tempPath);

        var buffer = new byte[81920];
        long readSoFar = 0;
        int bytesRead;
        while ((bytesRead = await httpStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            readSoFar += bytesRead;
            if (totalBytes > 0) progress?.Report((double)readSoFar / totalBytes);
        }

        return tempPath;
    }

    /// <summary>
    /// Launches the downloaded installer. Setup requires admin (RequestExecutionLevel
    /// admin in the NSIS script), so this triggers a UAC prompt — the caller should
    /// close the running app right after so the installer can overwrite the exe.
    /// </summary>
    public static void LaunchInstaller(string installerPath) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
        });

    private static bool IsNewer(string current, string latest)
    {
        if (!Version.TryParse(NormalizeForParse(current), out var currentV)) return false;
        if (!Version.TryParse(NormalizeForParse(latest),  out var latestV))  return false;
        return latestV > currentV;
    }

    // Version.Parse needs at least Major.Minor — pads a bare "1" to "1.0", and
    // strips any pre-release suffix (e.g. "1.0.6-beta") that Version can't parse.
    private static string NormalizeForParse(string v)
    {
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        return v.Count(c => c == '.') == 0 ? v + ".0" : v;
    }
}
