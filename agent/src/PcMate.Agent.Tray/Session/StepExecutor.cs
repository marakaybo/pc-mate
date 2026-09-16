using System.Diagnostics;
using System.Runtime.Versioning;
using PcMate.Agent.Tray.Interop;
using PcMate.Core.Ipc;
using PcMate.Core.Models;

namespace PcMate.Agent.Tray.Session;

/// <summary>
/// Выполняет шаги сценария, которым нужен рабочий стол пользователя.
/// Служба в Session 0 этого сделать не может — окна появились бы «в никуда».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StepExecutor
{
    public async Task<StepExecResult> ExecuteAsync(ScenarioStep step, CancellationToken ct = default)
    {
        try
        {
            var message = step.Type switch
            {
                StepTypes.Launch => await LaunchAsync(step, ct).ConfigureAwait(false),
                StepTypes.Open => Open(step),
                StepTypes.Url => OpenUrl(step),
                StepTypes.Close => CloseProcess(step),
                StepTypes.Volume => SetVolume(step),
                StepTypes.Uwp => LaunchUwp(step),
                _ => throw new InvalidOperationException($"Помощник не умеет выполнять шаг «{step.Type}».")
            };

            return new StepExecResult { Ok = true, Message = message };
        }
        catch (Exception ex)
        {
            return new StepExecResult { Ok = false, Message = ex.Message };
        }
    }

    private static async Task<string> LaunchAsync(ScenarioStep step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.Path))
            throw new InvalidOperationException("Не указан путь к программе.");

        if (!File.Exists(step.Path) && !Directory.Exists(step.Path))
            throw new FileNotFoundException($"Программа не найдена: {step.Path}");

        var psi = new ProcessStartInfo
        {
            FileName = step.Path!,
            Arguments = step.Args ?? "",
            WorkingDirectory = step.Cwd ?? Path.GetDirectoryName(step.Path) ?? "",
            UseShellExecute = true,
            WindowStyle = step.Window switch
            {
                "min" => ProcessWindowStyle.Minimized,
                "max" => ProcessWindowStyle.Maximized,
                _ => ProcessWindowStyle.Normal
            }
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Windows не смогла запустить программу.");

        if (step.WaitForExit)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, step.TimeoutSec)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return $"Запущено и завершено: {Path.GetFileName(step.Path)} (код {process.ExitCode})";
            }
            catch (OperationCanceledException)
            {
                return $"Запущено: {Path.GetFileName(step.Path)} (ещё работает)";
            }
        }

        return $"Запущено: {Path.GetFileName(step.Path)}";
    }

    private static string Open(ScenarioStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Path))
            throw new InvalidOperationException("Не указан путь к файлу или папке.");

        var isDirectory = Directory.Exists(step.Path);
        if (!isDirectory && !File.Exists(step.Path))
            throw new FileNotFoundException($"Не найдено: {step.Path}");

        Process.Start(new ProcessStartInfo { FileName = step.Path!, UseShellExecute = true })?.Dispose();
        return isDirectory ? $"Открыта папка {step.Path}" : $"Открыт файл {Path.GetFileName(step.Path)}";
    }

    private static string OpenUrl(ScenarioStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Url))
            throw new InvalidOperationException("Не указан адрес сайта.");

        var url = step.Url!.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Некорректный адрес: {step.Url}");

        if (!string.IsNullOrWhiteSpace(step.Browser) && File.Exists(step.Browser))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = step.Browser!,
                Arguments = $"\"{uri}\"",
                UseShellExecute = true
            })?.Dispose();
            return $"Открыт {uri.Host} в {Path.GetFileNameWithoutExtension(step.Browser)}";
        }

        Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true })?.Dispose();
        return $"Открыт {uri.Host}";
    }

    private static string CloseProcess(ScenarioStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Process))
            throw new InvalidOperationException("Не указано имя процесса.");

        var name = step.Process!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? step.Process[..^4]
            : step.Process;

        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 0) return $"Процесс {name} не запущен";

        var closed = 0;
        foreach (var process in processes)
        {
            try
            {
                if (step.Force)
                {
                    process.Kill(entireProcessTree: true);
                    closed++;
                }
                else
                {
                    // Мягко: просим окно закрыться, даём сохраниться.
                    if (process.CloseMainWindow() || process.HasExited) closed++;
                }
            }
            catch
            {
                // Процесс мог закрыться сам или принадлежать другому пользователю.
            }
            finally
            {
                process.Dispose();
            }
        }

        return $"Закрыто процессов: {closed} из {processes.Length}";
    }

    private static string SetVolume(ScenarioStep step)
    {
        if (step.Mute is not null)
        {
            VolumeControl.SetMute(step.Mute.Value);
            if (step.Level is null) return step.Mute.Value ? "Звук выключен" : "Звук включён";
        }

        if (step.Level is null) throw new InvalidOperationException("Не указан уровень громкости.");

        VolumeControl.SetLevel(step.Level.Value);
        return $"Громкость: {step.Level.Value}%";
    }

    private static string LaunchUwp(ScenarioStep step)
    {
        if (string.IsNullOrWhiteSpace(step.AppId))
            throw new InvalidOperationException("Не указан идентификатор приложения.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{step.AppId}",
            UseShellExecute = true
        })?.Dispose();

        return $"Запущено приложение {step.AppId}";
    }
}
