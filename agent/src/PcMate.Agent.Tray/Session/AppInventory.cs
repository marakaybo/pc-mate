using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using PcMate.Core.Models;

namespace PcMate.Agent.Tray.Session;

/// <summary>
/// Список установленных программ для конструктора сценариев на телефоне:
/// ярлыки меню «Пуск» + приложения Магазина. Путь вручную вводить не нужно.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppInventory
{
    private static readonly string[] SkipKeywords =
    {
        "uninstall", "удалить", "деинсталл", "readme", "документация", "help", "справка",
        "website", "сайт", "license", "лицензи", "changelog", "report a", "manual"
    };

    private readonly object _gate = new();
    private List<AppEntry>? _cache;
    private DateTime _cachedAt = DateTime.MinValue;

    public List<AppEntry> Get(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(10))
                return _cache;

            var apps = Collect();
            _cache = apps;
            _cachedAt = DateTime.UtcNow;
            return apps;
        }
    }

    private static List<AppEntry> Collect()
    {
        var byPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in CollectStartMenu())
        {
            var key = entry.Path ?? entry.Name;
            if (!byPath.ContainsKey(key)) byPath[key] = entry;
        }

        foreach (var entry in CollectUwp())
        {
            var key = entry.UwpAppId ?? entry.Name;
            if (!byPath.ContainsKey(key)) byPath[key] = entry;
        }

        return byPath.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<AppEntry> CollectStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (ShouldSkip(name)) continue;

                var link = ShellLink.Read(file);
                if (link is null || !link.HasTarget) continue;

                var target = link.TargetPath!;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(target)) continue;

                yield return new AppEntry
                {
                    Name = name,
                    Path = target,
                    Args = link.Arguments,
                    Cwd = link.WorkingDirectory,
                    Source = "start-menu",
                    IconB64 = ExtractIcon(target)
                };
            }
        }
    }

    /// <summary>Приложения Магазина: Get-StartApps даёт и имя, и AppID для shell:AppsFolder.</summary>
    private static IEnumerable<AppEntry> CollectUwp()
    {
        var results = new List<AppEntry>();
        try
        {
            var script = "Get-StartApps | Where-Object { $_.AppID -like '*!*' } | " +
                         "ForEach-Object { \"$($_.Name)`t$($_.AppID)\" }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process is null) return results;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(20000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length != 2) continue;
                if (ShouldSkip(parts[0])) continue;

                results.Add(new AppEntry
                {
                    Name = parts[0].Trim(),
                    UwpAppId = parts[1].Trim(),
                    Source = "uwp"
                });
            }
        }
        catch
        {
            // Список приложений Магазина не критичен — ярлыков обычно достаточно.
        }

        return results;
    }

    private static bool ShouldSkip(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var lower = name.ToLowerInvariant();
        return SkipKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private static string? ExtractIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;

            using var source = icon.ToBitmap();
            using var resized = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, 32, 32);
            }

            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
