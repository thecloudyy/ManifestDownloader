using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManifestDownloader;

/// <summary>Game library like Element's Manage page: enumerate installed luas,
/// resolve names (bulk applist → lua header → App {id}), fetch cover art.</summary>
internal static class LibraryService
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan AppListTtl = TimeSpan.FromDays(7);

    static LibraryService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ManifestDownloader/1.0");
        Http.Timeout = TimeSpan.FromSeconds(60);
    }

    internal sealed record LibraryEntry(long AppId, string Path);

    internal static IEnumerable<LibraryEntry> EnumerateInstalled(string stPlugInDir)
    {
        if (!Directory.Exists(stPlugInDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(stPlugInDir, "*.lua"))
            if (long.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out long appId))
                yield return new LibraryEntry(appId, path);
    }

    /// <summary>Name from the lua header: "-- App Name: X" on line 1 (ManifestDeX
    /// style), else Element's "-- Created by" + "-- Name" pattern.</summary>
    internal static string? ParseLuaName(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? line0 = reader.ReadLine();
            string? line1 = reader.ReadLine();
            if (line0 is null) return null;
            const string marker = "-- App Name:";
            int i = line0.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string name = line0[(i + marker.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            if (line1 is not null &&
                line0.Contains("Created by", StringComparison.OrdinalIgnoreCase) &&
                line1.StartsWith("--"))
            {
                string name = line1.TrimStart('-', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch { }
        return null;
    }

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ManifestDownloader");

    /// <summary>Bulk appid→name map, cached on disk. Same source as Element's primary.</summary>
    internal static async Task<Dictionary<long, string>> GetAppListAsync(CancellationToken ct)
    {
        string cachePath = Path.Combine(AppDataDir, "applist.json");
        try
        {
            if (File.Exists(cachePath) && DateTime.Now - File.GetLastWriteTime(cachePath) < AppListTtl)
                return ReadAppList(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false));
        }
        catch { }
        try
        {
            using var res = await Http.GetAsync("https://applist.morrenus.xyz/", ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var map = ReadAppList(json);
                if (map.Count > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(AppDataDir);
                        await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
                    }
                    catch { }
                    return map;
                }
            }
        }
        catch { }
        try
        {
            if (File.Exists(cachePath))
                return ReadAppList(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false));
        }
        catch { }
        return new Dictionary<long, string>();
    }

    private static Dictionary<long, string> ReadAppList(string json)
    {
        var map = new Dictionary<long, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("appid", out var id) && el.TryGetProperty("name", out var name) &&
                    id.TryGetInt64(out long appId) && name.GetString() is { Length: > 0 } n)
                    map[appId] = n;
            }
        }
        catch { }
        return map;
    }

    internal static string ResolveName(long appId, string luaPath, Dictionary<long, string> applist) =>
        applist.TryGetValue(appId, out var n) && !string.IsNullOrWhiteSpace(n) ? n
        : ParseLuaName(luaPath) ?? $"App {appId}";

    /// <summary>Local cover path. Tries header → library capsule → small capsule,
    /// atomic write, cached files re-validated. Null when the CDN has no art.</summary>
    internal static async Task<string?> EnsureCoverAsync(long appId, CancellationToken ct)
    {
        string dir = Path.Combine(AppDataDir, "covers");
        string path = Path.Combine(dir, $"{appId}.jpg");
        if (IsValidImage(path)) return path;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        string[] urls =
        [
            Cdn(appId, "header.jpg"),
            Cdn(appId, "capsule_616x353.jpg"),
            Cdn(appId, "library_600x900.jpg"),
            Cdn(appId, "hero_capsule.jpg"),
            Cdn(appId, "library_hero.jpg"),
            Cdn(appId, "capsule_184x69.jpg"),
            Cdn(appId, "logo.png"),
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_616x353.jpg",
            $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg",
        ];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? hit = await DownloadFirstValidAsync(urls, path, ct);
            if (hit is not null) return hit;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
        }
        return null;
    }

    /// <summary>Client/community icon from appinfo — exists even for games with
    /// no store art at all. Same cache path as covers.</summary>
    internal static async Task<string?> EnsureCommunityIconAsync(long appId, CancellationToken ct)
    {
        string dir = Path.Combine(AppDataDir, "covers");
        string path = Path.Combine(dir, $"{appId}.jpg");
        try
        {
            using var info = await ManifestService.GetAppInfoAsync(appId.ToString(), ct).ConfigureAwait(false);
            if (info is null) return null;
            var hashes = new List<string>();
            if (info.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(appId.ToString(), out var app) &&
                app.TryGetProperty("common", out var common))
            {
                if (common.TryGetProperty("clienticon", out var ci) && ci.GetString() is { Length: > 0 } a)
                    hashes.Add(a);
                if (common.TryGetProperty("icon", out var ic) && ic.GetString() is { Length: > 0 } b && b != (hashes.Count > 0 ? hashes[0] : null))
                    hashes.Add(b);
            }
            if (hashes.Count == 0) return null;
            var urls = hashes.Select(h =>
                $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{h}.jpg");
            return await DownloadFirstValidAsync(urls, path, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>First URL that returns valid image bytes, atomically cached. Null if none.</summary>
    private static async Task<string?> DownloadFirstValidAsync(IEnumerable<string> urls, string path, CancellationToken ct)
    {
        foreach (string url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue;
                byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!IsJpegBytes(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    private static string Cdn(long appId, string file) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{file}";

    private static bool IsValidImage(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 512) return false;
            byte[] head = new byte[4];
            using var fs = File.OpenRead(path);
            if (fs.Read(head, 0, 4) < 4) return false;
            return IsJpeg(head) || IsPng(head);
        }
        catch { return false; }
    }

    private static bool IsJpegBytes(byte[] b) => b.Length >= 512 && IsJpeg(b);

    private static bool IsJpeg(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsPng(byte[] b) =>
        b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Monogram tile for games with no CDN art, so every row shows
    /// something — gradient hue derived from the AppID. Cached as {id}.placeholder.png.</summary>
    internal static string? GeneratePlaceholder(long appId, string name)
    {
        try
        {
            string dir = Path.Combine(AppDataDir, "covers");
            string path = Path.Combine(dir, $"{appId}.placeholder.png");
            if (File.Exists(path) && new FileInfo(path).Length >= 512) return path;
            var words = name.Split([' ', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            string initial = words.Length switch
            {
                0 => "?",
                1 => words[0].Length > 0 ? char.ToUpperInvariant(words[0][0]).ToString() : "?",
                _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
            };
            const int w = 184, h = 86;
            // Stable per-app hue so every game gets a distinctive, theme-friendly tile.
            double hue = ((appId % 360) * 47 + 360) % 360;
            var from = HslToRgb(hue, 0.44, 0.33);
            var to = HslToRgb((hue + 34) % 360, 0.52, 0.15);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(from, to, 55), null, new Rect(0, 0, w, h));
                var text = new FormattedText(initial,
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    40, new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)), 1.0);
                dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            enc.Save(fs);
            return path;
        }
        catch { return null; }
    }
}
