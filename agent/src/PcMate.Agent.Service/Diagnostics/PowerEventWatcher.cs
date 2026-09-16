using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Services;
using PcMate.Agent.Service.State;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Diagnostics;

/// <summary>
/// Определяет факт пробуждения компьютера. Приём простой и работает и в службе,
/// и в консольном режиме: во сне системный счётчик тиков не идёт, а настенные часы идут.
/// Расхождение между ними и есть время, проведённое во сне.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerEventWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SuspendThreshold = TimeSpan.FromSeconds(25);

    private readonly ILogger<PowerEventWatcher> _log;
    private readonly AgentEventBus _bus;
    private readonly AgentStore _store;
    private readonly WakeTestService _wakeTest;

    private DateTime _lastWallClock = DateTime.UtcNow;
    private ulong _lastTicks = NativeMethods.GetTickCount64();

    public PowerEventWatcher(ILogger<PowerEventWatcher> log, AgentEventBus bus, AgentStore store,
        WakeTestService wakeTest)
    {
        _log = log;
        _bus = bus;
        _store = store;
        _wakeTest = wakeTest;
    }

    public event Func<TimeSpan, CancellationToken, Task>? Resumed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // При старте службы после пробуждения тест мог остаться незавершённым.
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        await _wakeTest.OnResumeAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var ticks = NativeMethods.GetTickCount64();

                var wallDelta = now - _lastWallClock;
                var tickDelta = TimeSpan.FromMilliseconds(ticks - _lastTicks);
                var gap = wallDelta - tickDelta;

                _lastWallClock = now;
                _lastTicks = ticks;

                if (gap > SuspendThreshold)
                    await OnResumeAsync(gap, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка наблюдателя событий питания");
            }
        }
    }

    private async Task OnResumeAsync(TimeSpan slept, CancellationToken ct)
    {
        var reason = await WakeTestService.GetLastWakeReasonAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Компьютер проснулся после {Minutes:0.#} мин сна. Причина: {Reason}",
            slept.TotalMinutes, reason);
        _store.AppendEvent("power.wake", $"Компьютер проснулся ({reason})");

        await _bus.PublishAsync(Events.PowerWake, new
        {
            reason,
            sleptSeconds = (long)slept.TotalSeconds,
            at = TimeUtil.IsoNow()
        }, ct).ConfigureAwait(false);

        await _bus.PublishAsync(Events.PowerState, new { state = PowerStates.Running, reason = "проснулся" }, ct)
            .ConfigureAwait(false);

        await _wakeTest.OnResumeAsync(ct).ConfigureAwait(false);

        if (Resumed is not null)
            await Resumed(slept, ct).ConfigureAwait(false);
    }
}
