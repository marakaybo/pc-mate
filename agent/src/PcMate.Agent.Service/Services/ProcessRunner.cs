using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PcMate.Agent.Service.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string All => (StdOut + "\n" + StdErr).Trim();
}

/// <summary>Запуск консольных утилит Windows (powercfg, schtasks, powershell) с корректной кодировкой.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var oemEncoding = GetOemEncoding();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = oemEncoding,
            StandardErrorEncoding = oemEncoding
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) return new ProcessResult(-1, "", $"Не удалось запустить {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            return new ProcessResult(-2, "", $"Команда {fileName} не завершилась за отведённое время.");
        }

        return new ProcessResult(process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    /// <summary>PowerShell без профиля — для командлетов Net*Adapter*.</summary>
    public static Task<ProcessResult> PowerShellAsync(string script, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout ?? TimeSpan.FromSeconds(45), ct);
    }

    public static Task<ProcessResult> PowerCfgAsync(string arguments, CancellationToken ct = default)
        => RunAsync("powercfg.exe", arguments, TimeSpan.FromSeconds(30), ct);

    private static Encoding GetOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
