using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PcMate.Agent.Tray.Session;

/// <summary>
/// Проверка обновлений через GitHub Releases.
///
/// PC MATE ставится установщиком с GitHub, а не из магазина: никто не обновит
/// его автоматически. Раз в сутки спрашиваем у GitHub последнюю версию и,
/// если вышла новее, показываем подсказку в трее.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateChecker
{
    /// <summary>Репозиторий, откуда берутся обновления.</summary>
    public const string Owner = "marakaybo";
    public const string Repo = "pc-mate";

    private const string RegistryKey = @"SOFTWARE\PC MATE";
    private const string LastCheckValue = "LastUpdateCheck";
    private const string SkippedValue = "SkippedVersion";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public sealed record UpdateInfo(string Version, string CurrentVersion, string Notes, string ReleaseUrl,
        string? SetupUrl);

    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// Возвращает информацию об обновлении или null. Любая сетевая ошибка —
    /// это null: проверка обновлений не должна мешать работе агента.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !ShouldCheckNow()) return null;

        GithubRelease? release;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PC-MATE", CurrentVersion));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            release = await http.GetFromJsonAsync<GithubRelease>(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            RememberCheck();
        }

        if (release is null || release.Draft || release.Prerelease) return null;

        var latest = (release.TagName ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(latest, out var latestVersion)) return null;
        if (!Version.TryParse(CurrentVersion, out var current)) return null;
        if (latestVersion <= current) return null;

        if (!force && string.Equals(ReadString(SkippedValue), latest, StringComparison.Ordinal)) return null;

        var setup = release.Assets?
            .FirstOrDefault(a => a.Name is not null &&
                                 a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                 a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(
            latest,
            CurrentVersion,
            Trim(release.Body ?? ""),
            release.HtmlUrl ?? ReleasesUrl,
            setup?.BrowserDownloadUrl);
    }

    public static void SkipVersion(string version) => WriteString(SkippedValue, version);

    private static bool ShouldCheckNow()
    {
        var last = ReadString(LastCheckValue);
        if (!DateTimeOffset.TryParse(last, out var when)) return true;
        return DateTimeOffset.UtcNow - when > CheckInterval;
    }

    private static void RememberCheck() => WriteString(LastCheckValue, DateTimeOffset.UtcNow.ToString("O"));

    private static string? ReadString(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch
        {
            // Не смогли запомнить — просто проверим ещё раз при следующем запуске.
        }
    }

    private static string Trim(string notes)
    {
        var text = notes.Replace("\r\n", "\n").Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
