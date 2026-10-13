using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ManifestDownloader;

/// <summary>Fallback API source (tried after the direct sources).</summary>
internal enum ManifestMode
{
    GitHub,           // no fallback API
    GitHubMorrenus,
    GitHubManifestHub,
}

/// <summary>Which download sources a run may use. The attempt order is
/// GitHub mirror → ManifestDeX → fallback API, skipping disabled sources.</summary>
internal sealed record DownloadSources(ManifestMode ApiMode, bool Github, bool ManifestDeX)
{
    internal bool NeedsKey => ApiMode is ManifestMode.GitHubMorrenus or ManifestMode.GitHubManifestHub;
    internal bool AnyEnabled => Github || ManifestDeX || NeedsKey;
}

internal sealed record DepotJob(string DepotId, string ManifestId);

internal sealed record ManifestResult(
    bool Success, long Size, int Attempts, string? Error, string? FilePath);

/// <summary>Port of the Steam Manifest Downloader script: lua parsing, app-info
/// lookup, and manifest fetching (GitHub mirror first, Morrenus/ManifestHub fallback).</summary>
internal static class ManifestService
{
    internal static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static ManifestService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ManifestDownloader/1.0");
        Http.Timeout = TimeSpan.FromSeconds(130);
    }

    // ── Steam detection (same method as Element's SteamService) ──

    private static readonly (RegistryHive Hive, string SubKey, string Value)[] RegistryLocations =
    [
        (RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    internal static string? DetectSteamDir()
    {
        foreach (var (hive, subKey, value) in RegistryLocations)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey);
                if (key?.GetValue(value) is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    string p = Normalize(raw);
                    if (File.Exists(Path.Combine(p, "steam.exe")))
                        return p;
                }
            }
            catch { }
        }
        return null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }
    }

    // ── Lua parsing: addappid(depotid, digit, "hexkey"), comments ignored by the pattern ──

    private static readonly Regex AddAppIdRx = new(
        @"addappid\s*\(\s*(\d+)\s*,\s*\d+\s*,\s*""[a-fA-F0-9]+""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static IReadOnlyList<string> GetDepotIdsFromLua(string luaPath) =>
        File.ReadLines(luaPath)
            .Select(l => AddAppIdRx.Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

    // ── SteamCMD app info ──

    internal static async Task<JsonDocument?> GetAppInfoAsync(string appId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var res = await Http.GetAsync($"https://api.steamcmd.net/v1/info/{appId}", timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("status", out var st) &&
                st.GetString()?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                return doc;
            doc.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // user pressed Cancel
        catch { return null; } // HTTP error or the 30s lookup timeout
    }

    internal static string? GetManifestId(JsonDocument appInfo, string appId, string depotId)
    {
        try
        {
            if (appInfo.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(appId, out var app) &&
                app.TryGetProperty("depots", out var depots) &&
                depots.TryGetProperty(depotId, out var depot) &&
                depot.TryGetProperty("manifests", out var manifests) &&
                manifests.TryGetProperty("public", out var pub) &&
                pub.TryGetProperty("gid", out var gid))
                return gid.GetString();
        }
        catch { }
        return null;
    }

    // ── Morrenus key validation ──

    internal static bool IsValidMorrenusFormat(string key) =>
        Regex.IsMatch(key, @"^smm_[0-9a-f]{96}$");

    internal static async Task<(bool Ok, string Message)> ValidateMorrenusKeyAsync(string key, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var res = await Http.GetAsync(
                $"https://hubcapmanifest.com/api/v1/user/stats?api_key={Uri.EscapeDataString(key)}", timeout.Token).ConfigureAwait(false);
            string body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("detail", out var d))
                        return (false, d.GetString() ?? res.ReasonPhrase ?? "Invalid key.");
                }
                catch { }
                return (false, (int)res.StatusCode is 401 or 403 or 404
                    ? "API key not found or expired."
                    : $"Key check failed: {(int)res.StatusCode} {res.ReasonPhrase}.");
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool canMake = !root.TryGetProperty("can_make_requests", out var c) || c.GetBoolean();
            if (!canMake)
            {
                string usage = root.TryGetProperty("daily_usage", out var u) ? u.ToString() : "?";
                string limit = root.TryGetProperty("daily_limit", out var l) ? l.ToString() : "?";
                return (false, $"Key hit its daily limit ({usage}/{limit}). Try again tomorrow.");
            }
            string user = root.TryGetProperty("username", out var n) ? n.GetString() ?? "user" : "user";
            return (true, $"Welcome back {user}!");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The 15s linked timeout fired — a real failure, not the user pressing
            // Cancel (an OCE from ct must still propagate to the run's cancel path).
            return (false, "Key check timed out — check your connection and try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"Failed to validate key: {ex.Message}");
        }
    }

    // ── Manifest download: GitHub first (2 tries), then secondary API on 404 (5 tries) ──

    internal static async Task<ManifestResult> DownloadManifestAsync(
        DepotJob job, string outputDir, DownloadSources sources, string? apiKey,
        IProgress<(long Done, long? Total)>? fileProgress, CancellationToken ct,
        int retryDelaySeconds = 3)
    {
        string outputFile = Path.Combine(outputDir, $"{job.DepotId}_{job.ManifestId}.manifest");

        int attempts = 0;
        string? lastError = null;

        // 1. GitHub mirror (optional).
        if (sources.Github)
        {
            string githubUrl = $"https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main/{job.DepotId}_{job.ManifestId}.manifest";
            var github = await TryDownloadUrlAsync(githubUrl, outputFile, maxRetries: 2, fileProgress, ct, retryDelaySeconds).ConfigureAwait(false);
            attempts += github.Attempts;
            if (github.Success)
                return new ManifestResult(true, github.Size, attempts, null, outputFile);
            lastError = github.Error;
        }

        // 2. ManifestDeX code API + Steam CDN (optional, keyless).
        if (sources.ManifestDeX)
        {
            var deX = await DownloadViaManifestDeXAsync(job, outputFile, fileProgress, ct, retryDelaySeconds).ConfigureAwait(false);
            attempts += deX.Attempts;
            if (deX.Success)
                return new ManifestResult(true, deX.Size, attempts, null, outputFile);
            lastError = deX.Error ?? lastError;
        }

        // 3. Morrenus / ManifestHub API (optional, needs a key).
        if (sources.NeedsKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            string secondaryUrl = sources.ApiMode == ManifestMode.GitHubMorrenus
                ? $"https://hubcapmanifest.com/api/v1/generate/manifest?depot_id={job.DepotId}&manifest_id={job.ManifestId}&api_key={Uri.EscapeDataString(apiKey)}"
                : $"https://api.manifesthub1.filegear-sg.me/manifest?apikey={Uri.EscapeDataString(apiKey)}&depotid={job.DepotId}&manifestid={job.ManifestId}";
            var secondary = await TryDownloadUrlAsync(secondaryUrl, outputFile, maxRetries: 5, fileProgress, ct, retryDelaySeconds).ConfigureAwait(false);
            attempts += secondary.Attempts;
            if (secondary.Success)
                return new ManifestResult(true, secondary.Size, attempts, null, outputFile);
            lastError = secondary.Error ?? lastError;
        }

        return new ManifestResult(false, 0, attempts,
            lastError ?? "No download source enabled.", null);
    }

    // ── ManifestDeX: request-code API + Steam CDN (zip container) ──

    /// <summary>Docs/landing page for the ManifestDeX code API.</summary>
    internal const string ManifestDeXDocsUrl = "https://manifest.manifestdex.com/";

    /// <summary>Code endpoint tells us the CDN host in this response header;
    /// this is the fallback when it is missing (observed on the API itself).</summary>
    private const string ManifestDeXCdnHeader = "x-manifest-cdn";
    private const string ManifestDeXCdnFallback = "https://cache7-ord1.steamcontent.com";

    private sealed record DeXCode(string Code, string? Cdn);

    /// <summary>Client-side throttle: the service allows 60 requests/minute and
    /// batches resolve depots in parallel, so space code calls out globally.</summary>
    private static readonly SemaphoreSlim DeXCodeGate = new(1, 1);
    private static long _lastDeXTick;

    /// <summary>GET /{manifestId} → plain-text request code (uint64). Handles
    /// 429 (Retry-After) and 502/504 with a bounded retry. Null on failure.</summary>
    private static async Task<DeXCode?> GetManifestDeXCodeAsync(string manifestId, CancellationToken ct)
    {
        if (!ulong.TryParse(manifestId, out ulong gid) || gid == 0) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // ≤60 req/min regardless of how many depots are in flight.
            await DeXCodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long waitMs = 1100 - (Environment.TickCount64 - Interlocked.Read(ref _lastDeXTick));
                if (waitMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastDeXTick, Environment.TickCount64);
            }
            finally { DeXCodeGate.Release(); }

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://manifest.manifestdex.com/{gid}");
            req.Headers.TryAddWithoutValidation("User-Agent", "ManifestDeX/1.0"); // required by the API
            try
            {
                using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    string body = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                    if (ulong.TryParse(body, out ulong code) && code != 0)
                    {
                        string? cdn = res.Headers.TryGetValues(ManifestDeXCdnHeader, out var vals)
                            ? vals.FirstOrDefault()?.Trim().TrimEnd('/')
                            : null;
                        return new DeXCode(body, string.IsNullOrEmpty(cdn) ? null : cdn);
                    }
                    return null; // 200 but unusable body — retrying won't help
                }

                if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    double secs = res.Headers.RetryAfter?.Delta?.TotalSeconds
                        ?? res.Headers.RetryAfter?.Date?.ToUniversalTime().Subtract(DateTime.UtcNow).TotalSeconds
                        ?? 10;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(secs, 1, 30)), ct).ConfigureAwait(false);
                    continue;
                }
                if ((int)res.StatusCode is 502 or 504)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) // network errors AND HttpClient timeouts — both retryable
            {
                if (attempt < 2)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>Download a manifest through ManifestDeX: get the request code,
    /// fetch depot/{depot}/manifest/{id}/5/{code} from the Steam CDN (returns a
    /// zip), extract the single entry, and install it atomically.</summary>
    private static async Task<AttemptResult> DownloadViaManifestDeXAsync(
        DepotJob job, string outputFile,
        IProgress<(long Done, long? Total)>? fileProgress, CancellationToken ct, int retryDelaySeconds)
    {
        var deX = await GetManifestDeXCodeAsync(job.ManifestId, ct).ConfigureAwait(false);
        if (deX is null)
            return new AttemptResult(false, false, 0, 0, "ManifestDeX: could not get a request code");

        string cdn = deX.Cdn ?? ManifestDeXCdnFallback;
        string url = $"{cdn}/depot/{job.DepotId}/manifest/{job.ManifestId}/5/{deX.Code}";
        string zipFile = outputFile + ".zip";
        try
        {
            var fetched = await TryDownloadUrlAsync(
                url, zipFile, maxRetries: 3, fileProgress, ct, retryDelaySeconds).ConfigureAwait(false);
            if (!fetched.Success)
                return new AttemptResult(false, fetched.Is404, 0, fetched.Attempts, fetched.Error);

            string partFile = outputFile + ".part";
            try
            {
                TryDelete(partFile);
                using (var zipStream = File.OpenRead(zipFile))
                using (var zip = new System.IO.Compression.ZipArchive(
                           zipStream, System.IO.Compression.ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count == 0)
                        return new AttemptResult(false, false, 0, fetched.Attempts, "ManifestDeX zip has no entries");
                    await using var src = zip.Entries[0].Open();
                    await using var dst = File.Create(partFile);
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
                long size = new FileInfo(partFile).Length;
                if (size == 0)
                {
                    TryDelete(partFile);
                    return new AttemptResult(false, false, 0, fetched.Attempts, "ManifestDeX returned an empty manifest");
                }
                File.Move(partFile, outputFile, overwrite: true);
                return new AttemptResult(true, false, size, fetched.Attempts, null);
            }
            catch (OperationCanceledException) { TryDelete(partFile); throw; }
            catch (Exception ex) { TryDelete(partFile); return new AttemptResult(false, false, 0, fetched.Attempts, ex.Message); }
        }
        finally { TryDelete(zipFile); }
    }

    private sealed record AttemptResult(bool Success, bool Is404, long Size, int Attempts, string? Error);

    private static async Task<AttemptResult> TryDownloadUrlAsync(
        string url, string outputFile, int maxRetries,
        IProgress<(long Done, long? Total)>? fileProgress, CancellationToken ct, int retryDelaySeconds)
    {
        // Download into a .part sidecar and only rename on success: a crash or
        // cancel can never leave a half-written file behind.
        string tempFile = outputFile + ".part";
        string? lastError = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                TryDelete(tempFile);
                using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    TryDelete(tempFile);
                    return new AttemptResult(false, true, 0, attempt, "Not found (404)");
                }
                res.EnsureSuccessStatusCode();
                long? total = res.Content.Headers.ContentLength;
                await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                long done = 0;
                await using (var file = File.Create(tempFile))
                {
                    var buffer = new byte[81920];
                    int read;
                    long lastReport = 0;      // throttle progress callbacks (~8/s) instead of per-chunk
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    fileProgress?.Report((0, total));
                    // ResponseHeadersRead means HttpClient.Timeout no longer covers body
                    // reads — a dead connection would hang forever. Enforce a 60s
                    // inter-chunk idle limit ourselves, reset on every chunk received.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        try { read = await net.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException("Download stalled: no data for 60 seconds.");
                        }
                        if (read <= 0) break;
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if (clock.ElapsedMilliseconds - lastReport >= 120)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            fileProgress?.Report((done, total));
                        }
                    }
                    fileProgress?.Report((done, total));
                }
                if (new FileInfo(tempFile).Length > 0)
                {
                    File.Move(tempFile, outputFile, overwrite: true);
                    return new AttemptResult(true, false, done, attempt, null);
                }
                lastError = "Empty file received";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { TryDelete(tempFile); throw; }
            catch (TimeoutException ex) { lastError = ex.Message; }                      // our 60s stall guard
            catch (TaskCanceledException) { lastError = "Timed out (no response)."; }    // HttpClient.Timeout
            catch (Exception ex) { lastError = ex.Message; }

            if (attempt < maxRetries)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { TryDelete(tempFile); throw; }
            }
        }
        TryDelete(tempFile);
        return new AttemptResult(false, false, 0, maxRetries, lastError);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
