using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.State;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

public sealed class PendingWakeTest
{
    [JsonPropertyName("testId")] public string TestId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("mode")] public string Mode { get; set; } = "wol";     // wol | timer
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = TimeUtil.IsoNow();
    [JsonPropertyName("sleepSeconds")] public int SleepSeconds { get; set; } = 120;
    [JsonPropertyName("deadlineUnix")] public long DeadlineUnix { get; set; }
}

/// <summary>
/// Тестовое пробуждение (ТЗ §4.1.3): ПК уходит в сон, затем его будят
/// магическим пакетом или таймером. После пробуждения агент отчитывается,
/// сработало ли и что именно его разбудило.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WakeTestService
{
    public const string ModeWol = "wol";
    public const string ModeTimer = "timer";

    private readonly ILogger<WakeTestService> _log;
    private readonly AgentStore _store;
    private readonly PowerController _power;
    private readonly WakeTimerService _wakeTimers;
    private readonly AgentEventBus _bus;

    public WakeTestService(ILogger<WakeTestService> log, AgentStore store, PowerController power,
        WakeTimerService wakeTimers, AgentEventBus bus)
    {
        _log = log;
        _store = store;
        _power = power;
        _wakeTimers = wakeTimers;
        _bus = bus;
    }

    private string TestFilePath => Path.Combine(_store.RootPath, "wake-test.json");

    public PendingWakeTest? Pending
    {
        get
        {
            try
            {
                return File.Exists(TestFilePath)
                    ? PcMateJson.Deserialize<PendingWakeTest>(File.ReadAllText(TestFilePath))
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<PendingWakeTest> StartAsync(string mode, int sleepSeconds, CancellationToken ct = default)
    {
        sleepSeconds = Math.Clamp(sleepSeconds, 30, 900);
        var test = new PendingWakeTest
        {
            Mode = string.Equals(mode, ModeTimer, StringComparison.OrdinalIgnoreCase) ? ModeTimer : ModeWol,
            SleepSeconds = sleepSeconds,
            DeadlineUnix = TimeUtil.UnixNow() + sleepSeconds + 120
        };

        await File.WriteAllTextAsync(TestFilePath, PcMateJson.SerializePretty(test), ct).ConfigureAwait(false);

        if (test.Mode == ModeTimer)
        {
            var wakeAt = DateTime.Now.AddSeconds(sleepSeconds);
            await _wakeTimers.SetArriveTimerAsync(wakeAt, scenarioId: null, ct).ConfigureAwait(false);
            _log.LogInformation("Тест таймера пробуждения: ПК проснётся в {Time:HH:mm:ss}", wakeAt);
        }
        else
        {
            _log.LogInformation("Тест Wake-on-LAN: ПК засыпает, ждём магический пакет в течение {Sec} с", sleepSeconds);
        }

        _store.AppendEvent("wake.test", test.Mode == ModeTimer
            ? $"Начат тест таймера пробуждения ({sleepSeconds} с)"
            : $"Начат тест Wake-on-LAN ({sleepSeconds} с)");

        // Небольшая пауза, чтобы телефон успел получить ответ и показать инструкцию.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _power.ExecuteAsync(PowerActions.Sleep, force: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Не удалось усыпить ПК для теста пробуждения");
                await CompleteAsync(false, "Не удалось перевести компьютер в сон: " + ex.Message)
                    .ConfigureAwait(false);
            }
        }, CancellationToken.None);

        return test;
    }

    /// <summary>Вызывается после пробуждения: определяет, был ли тест успешным.</summary>
    public async Task OnResumeAsync(CancellationToken ct = default)
    {
        var test = Pending;
        if (test is null) return;

        var lastWake = await GetLastWakeReasonAsync(ct).ConfigureAwait(false);
        var elapsedMs = (TimeUtil.UnixNow() - DateTimeOffset.Parse(test.StartedAt).ToUnixTimeSeconds()) * 1000;
        var inTime = TimeUtil.UnixNow() <= test.DeadlineUnix;

        await CompleteAsync(inTime, lastWake, elapsedMs).ConfigureAwait(false);
    }

    private async Task CompleteAsync(bool ok, string? reason, long elapsedMs = 0)
    {
        var test = Pending;
        try { File.Delete(TestFilePath); } catch { /* уже удалён */ }
        if (test is null) return;

        if (test.Mode == ModeTimer)
            await _wakeTimers.CancelArriveTimerAsync().ConfigureAwait(false);

        _log.LogInformation("Тест пробуждения {Id} ({Mode}): {Result}. Причина: {Reason}",
            test.TestId, test.Mode, ok ? "успех" : "неудача", reason);

        _store.AppendEvent("wake.test.result",
            ok
                ? $"Тест {(test.Mode == ModeTimer ? "таймера" : "Wake-on-LAN")} пройден ({reason})"
                : $"Тест {(test.Mode == ModeTimer ? "таймера" : "Wake-on-LAN")} не пройден");

        await _bus.PublishAsync(Events.WakeTestResult, new
        {
            testId = test.TestId,
            mode = test.Mode,
            ok,
            elapsedMs,
            wakeReason = reason
        }).ConfigureAwait(false);
    }

    /// <summary>Причина последнего пробуждения (powercfg /lastwake).</summary>
    public static async Task<string> GetLastWakeReasonAsync(CancellationToken ct = default)
    {
        var res = await ProcessRunner.PowerCfgAsync("/lastwake", ct).ConfigureAwait(false);
        if (!res.Ok) return "неизвестно";

        var lines = res.StdOut
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Берём строки после «Wake History Count»/«Число элементов журнала» — там описание источника.
        var meaningful = lines
            .Where(l => l.Contains(':') && !l.StartsWith("Wake History", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return meaningful.Count > 0 ? string.Join("; ", meaningful.Take(3)) : "неизвестно";
    }
}
