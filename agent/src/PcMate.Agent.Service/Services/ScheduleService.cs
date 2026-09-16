using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Scenarios;
using PcMate.Agent.Service.State;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

public sealed class WakeTrigger
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("scenarioId")] public string? ScenarioId { get; set; }
    [JsonPropertyName("at")] public string At { get; set; } = TimeUtil.IsoNow();
}

/// <summary>
/// Расписания: заводит таймеры пробуждения в Планировщике, сам выполняет
/// «выключить/усыпить», подхватывает срабатывания таймеров через файлы-триггеры.
/// Работает без сервера — задачи уже лежат в Планировщике Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduleService : BackgroundService
{
    private readonly ILogger<ScheduleService> _log;
    private readonly AgentStore _store;
    private readonly WakeTimerService _wakeTimers;
    private readonly PowerController _power;
    private readonly ScenarioEngine _scenarios;
    private readonly AgentEventBus _bus;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastTick = DateTime.Now;

    public ScheduleService(ILogger<ScheduleService> log, AgentStore store, WakeTimerService wakeTimers,
        PowerController power, ScenarioEngine scenarios, AgentEventBus bus)
    {
        _log = log;
        _store = store;
        _wakeTimers = wakeTimers;
        _power = power;
        _scenarios = scenarios;
        _bus = bus;
    }

    public List<Schedule> List() => _store.LoadSchedules();

    public async Task<Schedule> SaveAsync(Schedule schedule, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = _store.LoadSchedules();
            var index = all.FindIndex(s => s.Id == schedule.Id);
            schedule.UpdatedAt = TimeUtil.IsoNow();

            if (index >= 0) all[index] = schedule;
            else all.Add(schedule);

            var synced = await _wakeTimers.SyncAsync(schedule, ct).ConfigureAwait(false);
            if (synced) schedule.SyncedAt = TimeUtil.IsoNow();

            _store.SaveSchedules(all);
            _store.AppendEvent("schedule.save", $"Расписание «{schedule.Name}» сохранено" + (synced ? "" : " (без таймера)"));
            return schedule;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = _store.LoadSchedules();
            var removed = all.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                _store.SaveSchedules(all);
                await _wakeTimers.RemoveAsync(id, ct).ConfigureAwait(false);
                _store.AppendEvent("schedule.delete", $"Расписание {id} удалено");
            }
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(int Synced, int Failed)> SyncAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = _store.LoadSchedules();
            var synced = 0;
            var failed = 0;

            foreach (var schedule in all)
            {
                var ok = await _wakeTimers.SyncAsync(schedule, ct).ConfigureAwait(false);
                if (ok)
                {
                    schedule.SyncedAt = TimeUtil.IsoNow();
                    synced++;
                }
                else
                {
                    failed++;
                }
            }

            _store.SaveSchedules(all);
            _log.LogInformation("Синхронизация расписаний: {Synced} успешно, {Failed} с ошибкой", synced, failed);
            return (synced, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Обрабатывает срабатывание таймера пробуждения (файл-триггер от задачи Планировщика).</summary>
    public async Task HandleTriggerAsync(WakeTrigger trigger, CancellationToken ct = default)
    {
        _log.LogInformation("Сработал таймер пробуждения: {Id}", trigger.Id);
        _store.AppendEvent("schedule.fired", $"Компьютер проснулся по расписанию ({trigger.Id})");

        await _bus.PublishAsync(Events.ScheduleFired, new
        {
            id = trigger.Id,
            action = ScheduleActions.Wake,
            at = trigger.At
        }, ct).ConfigureAwait(false);

        var scenarioId = trigger.ScenarioId;
        if (string.IsNullOrWhiteSpace(scenarioId))
            scenarioId = _store.LoadSchedules().FirstOrDefault(s => s.Id == trigger.Id)?.ScenarioId;

        if (string.IsNullOrWhiteSpace(scenarioId)) return;

        var scenario = _store.LoadScenarios().FirstOrDefault(s => s.Id == scenarioId);
        if (scenario is null)
        {
            _log.LogWarning("Сценарий {Id} из расписания не найден", scenarioId);
            return;
        }

        // Даём Windows восстановить сеанс и сеть после пробуждения.
        await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        _scenarios.Start(scenario, source: "schedule");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // При старте синхронизируем задачи: Windows могла сбросить их после обновления.
        try
        {
            await SyncAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось синхронизировать расписания при запуске");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTriggerFilesAsync(stoppingToken).ConfigureAwait(false);
                await ProcessDueSchedulesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка в цикле расписаний");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessTriggerFilesAsync(CancellationToken ct)
    {
        var dir = _wakeTimers.TriggersPath;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            WakeTrigger? trigger = null;
            try
            {
                trigger = PcMateJson.Deserialize<WakeTrigger>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Не удалось прочитать файл-триггер {File}", file);
            }
            finally
            {
                try { File.Delete(file); } catch { /* удалим в следующий раз */ }
            }

            if (trigger is not null)
                await HandleTriggerAsync(trigger, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessDueSchedulesAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var since = _lastTick;
        _lastTick = now;

        // Пропуск больше 10 минут — скорее всего компьютер спал; такие события не догоняем.
        if ((now - since).TotalMinutes > 10) return;

        var all = _store.LoadSchedules();
        var changed = false;

        foreach (var schedule in all.Where(s => s.Enabled && !s.NeedsWakeTimer))
        {
            var due = schedule.NextOccurrence(since);
            if (due is null || due > now) continue;

            _log.LogInformation("Расписание «{Name}» сработало ({Action})", schedule.Name, schedule.Action);
            schedule.LastFiredAt = TimeUtil.IsoNow();
            changed = true;

            await _bus.PublishAsync(Events.ScheduleFired, new
            {
                id = schedule.Id,
                action = schedule.Action,
                at = TimeUtil.IsoNow()
            }, ct).ConfigureAwait(false);

            await ExecuteScheduleActionAsync(schedule, ct).ConfigureAwait(false);
        }

        if (changed) _store.SaveSchedules(all);
    }

    private async Task ExecuteScheduleActionAsync(Schedule schedule, CancellationToken ct)
    {
        switch (schedule.Action)
        {
            case ScheduleActions.Shutdown:
            case ScheduleActions.Sleep:
            case ScheduleActions.Hibernate:
            case ScheduleActions.Reboot:
            {
                var action = schedule.Action == ScheduleActions.Reboot ? PowerActions.Reboot : schedule.Action;
                _store.AppendEvent("schedule.power",
                    $"Расписание «{schedule.Name}»: {PowerActions.RussianTitle(action)}");
                _power.Request(action, _store.Settings.CountdownSec, force: false, source: "schedule");
                break;
            }

            case ScheduleActions.Scenario:
            {
                var scenario = _store.LoadScenarios().FirstOrDefault(s => s.Id == schedule.ScenarioId);
                if (scenario is null)
                {
                    _log.LogWarning("Расписание «{Name}»: сценарий {Id} не найден", schedule.Name, schedule.ScenarioId);
                    return;
                }
                _scenarios.Start(scenario, source: "schedule");
                break;
            }
        }

        // Для разовых расписаний снимаем флаг «включено», чтобы они не висели в списке.
        if (!schedule.IsRecurring)
        {
            schedule.Enabled = false;
            await _wakeTimers.RemoveAsync(schedule.Id, ct).ConfigureAwait(false);
        }
    }
}
