using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.State;
using PcMate.Core.Models;

namespace PcMate.Agent.Service.Power;

/// <summary>
/// Таймеры пробуждения Windows. Это основной способ включить компьютер по расписанию:
/// задача в Планировщике с флагом WakeToRun работает из сна (S3) и гибернации (S4)
/// и не требует ни сети, ни сервера.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WakeTimerService
{
    public const string TaskFolder = "\\PC MATE";
    public const string TaskPrefix = "pcmate-";
    public const string ArriveTaskName = TaskPrefix + "arrive";

    private readonly ILogger<WakeTimerService> _log;
    private readonly AgentStore _store;

    public WakeTimerService(ILogger<WakeTimerService> log, AgentStore store)
    {
        _log = log;
        _store = store;
    }

    public string TriggersPath
    {
        get
        {
            var path = Path.Combine(_store.RootPath, "triggers");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string AgentExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return path;
            return Path.Combine(AppContext.BaseDirectory, "PcMate.Agent.Service.exe");
        }
    }

    public static string TaskNameFor(string scheduleId) => $"{TaskFolder}\\{TaskPrefix}{scheduleId}";

    /// <summary>Создаёт или пересоздаёт задачу пробуждения для расписания.</summary>
    public async Task<bool> SyncAsync(Schedule schedule, CancellationToken ct = default)
    {
        if (!schedule.NeedsWakeTimer)
        {
            // Действия «выключить/усыпить» выполняет сам агент — задача в Планировщике не нужна.
            await RemoveAsync(schedule.Id, ct).ConfigureAwait(false);
            return true;
        }

        if (!schedule.Enabled)
        {
            await RemoveAsync(schedule.Id, ct).ConfigureAwait(false);
            return true;
        }

        var next = schedule.NextOccurrence(DateTime.Now);
        if (next is null)
        {
            _log.LogWarning("Расписание {Id} не имеет будущих срабатываний — задача не создаётся.", schedule.Id);
            await RemoveAsync(schedule.Id, ct).ConfigureAwait(false);
            return false;
        }

        var xml = BuildTaskXml(schedule, next.Value);
        var xmlPath = Path.Combine(Path.GetTempPath(), $"pcmate-task-{schedule.Id}.xml");
        await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode, ct).ConfigureAwait(false);

        try
        {
            var (code, output) = await RunSchTasksAsync(
                $"/Create /TN \"{TaskNameFor(schedule.Id)}\" /XML \"{xmlPath}\" /F", ct).ConfigureAwait(false);

            if (code != 0)
            {
                _log.LogError("Не удалось создать задачу пробуждения для {Id}: {Output}", schedule.Id, output.Trim());
                return false;
            }

            _log.LogInformation("Задача пробуждения {Id} создана, ближайшее срабатывание {Next:dd.MM HH:mm}",
                schedule.Id, next.Value);
            return true;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* временный файл */ }
        }
    }

    public async Task<bool> RemoveAsync(string scheduleId, CancellationToken ct = default)
    {
        var (code, _) = await RunSchTasksAsync($"/Delete /TN \"{TaskNameFor(scheduleId)}\" /F", ct)
            .ConfigureAwait(false);
        return code == 0;
    }

    /// <summary>Разовое пробуждение «буду через N минут».</summary>
    public async Task<bool> SetArriveTimerAsync(DateTime wakeAt, string? scenarioId, CancellationToken ct = default)
    {
        var xml = BuildTaskXmlCore(
            description: "PC MATE: разовое пробуждение «Буду через…»",
            triggerXml: $"<TimeTrigger><StartBoundary>{wakeAt:yyyy-MM-ddTHH:mm:ss}</StartBoundary><Enabled>true</Enabled></TimeTrigger>",
            arguments: $"--fired arrive --scenario \"{scenarioId ?? ""}\"",
            deleteAfter: true);

        var xmlPath = Path.Combine(Path.GetTempPath(), "pcmate-task-arrive.xml");
        await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode, ct).ConfigureAwait(false);

        try
        {
            var (code, output) = await RunSchTasksAsync(
                $"/Create /TN \"{TaskFolder}\\{ArriveTaskName}\" /XML \"{xmlPath}\" /F", ct).ConfigureAwait(false);
            if (code != 0)
                _log.LogError("Не удалось создать таймер «Буду через…»: {Output}", output.Trim());
            return code == 0;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* временный файл */ }
        }
    }

    public async Task<bool> CancelArriveTimerAsync(CancellationToken ct = default)
    {
        var (code, _) = await RunSchTasksAsync($"/Delete /TN \"{TaskFolder}\\{ArriveTaskName}\" /F", ct)
            .ConfigureAwait(false);
        return code == 0;
    }

    public async Task<List<string>> ListTasksAsync(CancellationToken ct = default)
    {
        var (code, output) = await RunSchTasksAsync("/Query /FO LIST", ct).ConfigureAwait(false);
        if (code != 0) return new List<string>();

        return output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(TaskPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string BuildTaskXml(Schedule schedule, DateTime next)
    {
        var trigger = schedule.IsRecurring
            ? $"""
               <CalendarTrigger>
                       <StartBoundary>{next:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
                       <Enabled>true</Enabled>
                       <ScheduleByWeek>
                         <DaysOfWeek>{string.Concat(schedule.Days.Distinct().OrderBy(d => d).Select(DayElement))}</DaysOfWeek>
                         <WeeksInterval>1</WeeksInterval>
                       </ScheduleByWeek>
                     </CalendarTrigger>
               """
            : $"<TimeTrigger><StartBoundary>{next:yyyy-MM-ddTHH:mm:ss}</StartBoundary><Enabled>true</Enabled></TimeTrigger>";

        var args = $"--fired \"{schedule.Id}\"";
        if (!string.IsNullOrWhiteSpace(schedule.ScenarioId))
            args += $" --scenario \"{schedule.ScenarioId}\"";

        return BuildTaskXmlCore(
            description: $"PC MATE: {schedule.Name} ({schedule.DescribeDays()} в {schedule.Time})",
            triggerXml: trigger,
            arguments: args,
            deleteAfter: !schedule.IsRecurring);
    }

    private string BuildTaskXmlCore(string description, string triggerXml, string arguments, bool deleteAfter)
    {
        var battery = _store.Settings.WakeTimersOnBattery ? "false" : "true";
        var deleteExpired = deleteAfter
            ? "<DeleteExpiredTaskAfter>PT10M</DeleteExpiredTaskAfter>"
            : "";

        return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Author>PC MATE</Author>
                    <Description>{Escape(description)}</Description>
                  </RegistrationInfo>
                  <Triggers>
                    {triggerXml}
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>S-1-5-18</UserId>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>{battery}</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>true</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>false</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <WakeToRun>true</WakeToRun>
                    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                    <Priority>7</Priority>
                    {deleteExpired}
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{Escape(AgentExecutablePath)}</Command>
                      <Arguments>{Escape(arguments)}</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
    }

    private static string DayElement(int day) => day switch
    {
        0 => "<Sunday />",
        1 => "<Monday />",
        2 => "<Tuesday />",
        3 => "<Wednesday />",
        4 => "<Thursday />",
        5 => "<Friday />",
        6 => "<Saturday />",
        _ => ""
    };

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static async Task<(int Code, string Output)> RunSchTasksAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };

        using var process = Process.Start(psi);
        if (process is null) return (-1, "Не удалось запустить schtasks.exe");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (process.ExitCode, stdout + stderr);
    }
}
