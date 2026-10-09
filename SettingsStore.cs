using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManifestDownloader;

/// <summary>Persisted settings: source mode, last AppID, theme, window size, and
/// the API key (DPAPI-encrypted for the current Windows user, never plain text).</summary>
internal sealed class AppSettings
{
    public string Mode { get; set; } = "github+manifestdex";
    // Explicit source toggles — when present these take precedence over the
    // legacy Mode string (which older versions used for everything).
    public bool? UseGithub { get; set; }
    public bool? UseManifestDeX { get; set; }
    /// <summary>"none" | "morrenus" | "manifesthub".</summary>
    public string? ApiSource { get; set; }
    public string? ProtectedKey { get; set; }
    public bool RememberKey { get; set; } = true;
    public string? LastAppId { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}

internal static class SettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ManifestDownloader", "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    internal static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }

    internal static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        try
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        catch { return null; }
    }

    internal static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        try
        {
            byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }
}
