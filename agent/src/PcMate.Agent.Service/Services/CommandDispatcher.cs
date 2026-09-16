using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Ipc;
using PcMate.Agent.Service.Pairing;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Readiness;
using PcMate.Agent.Service.Scenarios;
using PcMate.Agent.Service.State;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

public enum CommandOrigin
{
    Phone,
    Local,
    Schedule
}

/// <summary>
/// Единая точка исполнения команд протокола. Сюда приходят команды с телефона
/// (через сервер или локальную сеть) и из трея — обработка одинаковая.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandDispatcher
{
    private readonly ILogger<CommandDispatcher> _log;
    private readonly AgentStore _store;
    private readonly StatusProvider _status;
    private readonly PowerController _power;
    private readonly ScenarioEngine _scenarios;
    private readonly ScheduleService _schedules;
    private readonly ReadinessChecker _readiness;
    private readonly PairingService _pairing;
    private readonly WakeTimerService _wakeTimers;
    private readonly WakeTestService _wakeTest;
    private readonly SessionHost _session;
    private readonly AgentEventBus _bus;

    public CommandDispatcher(ILogger<CommandDispatcher> log, AgentStore store, StatusProvider status,
        PowerController power, ScenarioEngine scenarios, ScheduleService schedules, ReadinessChecker readiness,
        PairingService pairing, WakeTimerService wakeTimers, WakeTestService wakeTest, SessionHost session,
        AgentEventBus bus)
    {
        _log = log;
        _store = store;
        _status = status;
        _power = power;
        _scenarios = scenarios;
        _schedules = schedules;
        _readiness = readiness;
        _pairing = pairing;
        _wakeTimers = wakeTimers;
        _wakeTest = wakeTest;
        _session = session;
        _bus = bus;
    }

    public async Task<ResPayload> ExecuteAsync(CmdPayload cmd, CommandOrigin origin, CancellationToken ct = default)
    {
        _log.LogInformation("Команда {Cmd} (источник: {Origin})", cmd.Cmd, origin);
        _store.AppendEvent("cmd", $"Команда {cmd.Cmd}", origin == CommandOrigin.Phone ? "phone" : "pc");

        try
        {
            return await DispatchAsync(cmd, origin, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ResPayload.Failure(ErrorCodes.Timeout, "Команда не успела выполниться.");
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Команда {Cmd} отклонена", cmd.Cmd);
            return ResPayload.Failure(ErrorCodes.Os, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ResPayload.Failure(ErrorCodes.BadArgs, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Команда {Cmd} завершилась ошибкой", cmd.Cmd);
            return ResPayload.Failure(ErrorCodes.Unknown, ex.Message);
        }
    }

    private async Task<ResPayload> DispatchAsync(CmdPayload cmd, CommandOrigin origin, CancellationToken ct)
    {
        var args = cmd.Args;

        switch (cmd.Cmd)
        {
            // -------- Статус --------

            case Commands.StatusGet:
                return ResPayload.Success(await _status.GetAsync(ct: ct).ConfigureAwait(false));

            case Commands.AgentInfo:
                return ResPayload.Success(_status.GetInfo());

            case Commands.HistoryList:
                return ResPayload.Success(new { events = _store.LoadEvents(GetInt(args, "limit") ?? 100) });

            // -------- Питание --------

            case Commands.PowerShutdown:
                return PowerResponse(PowerActions.Shutdown, args);

            case Commands.PowerReboot:
                return PowerResponse(PowerActions.Reboot, args);

            case Commands.PowerSleep:
                return PowerResponse(PowerActions.Sleep, args);

            case Commands.PowerHibernate:
                return PowerResponse(PowerActions.Hibernate, args);

            case Commands.PowerLock:
                return PowerResponse(PowerActions.Lock, args);

            case Commands.PowerCancel:
                return ResPayload.Success(new { cancelled = _power.Cancel() });

            // -------- Сценарии --------

            case Commands.ScenarioList:
                return ResPayload.Success(new { scenarios = _store.LoadScenarios() });

            case Commands.ScenarioGet:
            {
                var id = GetString(args, "id") ?? throw new ArgumentException("Не указан id сценария.");
                var scenario = _store.LoadScenarios().FirstOrDefault(s => s.Id == id);
                return scenario is null
                    ? ResPayload.Failure(ErrorCodes.NotFound, $"Сценарий {id} не найден.")
                    : ResPayload.Success(new { scenario });
            }

            case Commands.ScenarioSave:
            {
                var scenario = GetObject<Scenario>(args, "scenario")
                               ?? throw new ArgumentException("Не передан сценарий.");
                if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = Guid.NewGuid().ToString("N")[..8];
                scenario.UpdatedAt = TimeUtil.IsoNow();

                var all = _store.LoadScenarios();
                var index = all.FindIndex(s => s.Id == scenario.Id);
                if (index >= 0) all[index] = scenario;
                else all.Add(scenario);
                _store.SaveScenarios(all);
                _store.AppendEvent("scenario.save", $"Сценарий «{scenario.Name}» сохранён");

                return ResPayload.Success(new { id = scenario.Id });
            }

            case Commands.ScenarioDelete:
            {
                var id = GetString(args, "id") ?? throw new ArgumentException("Не указан id сценария.");
                var all = _store.LoadScenarios();
                var removed = all.RemoveAll(s => s.Id == id) > 0;
                if (removed) _store.SaveScenarios(all);
                return ResPayload.Success(new { deleted = removed });
            }

            case Commands.ScenarioRun:
            {
                var scenario = GetObject<Scenario>(args, "scenario");
                if (scenario is null)
                {
                    var id = GetString(args, "id") ?? throw new ArgumentException("Не указан сценарий.");
                    scenario = _store.LoadScenarios().FirstOrDefault(s => s.Id == id);
                    if (scenario is null)
                        return ResPayload.Failure(ErrorCodes.NotFound, $"Сценарий {id} не найден.");
                }

                var runId = _scenarios.Start(scenario, origin.ToString().ToLowerInvariant());
                return ResPayload.Success(new { runId });
            }

            case Commands.ScenarioCancel:
            {
                var runId = GetString(args, "runId") ?? throw new ArgumentException("Не указан runId.");
                return ResPayload.Success(new { cancelled = _scenarios.Cancel(runId) });
            }

            // -------- Программы --------

            case Commands.AppsList:
            {
                if (!_session.HasUserSession)
                    return ResPayload.Failure(ErrorCodes.NoSession,
                        "Список программ собирает помощник в сеансе пользователя, а сейчас никто не вошёл в Windows.");

                var apps = await _session.RequestAsync<List<AppEntry>>(
                    IpcKinds.AppsList,
                    new { refresh = GetBool(args, "refresh") ?? false },
                    TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);

                return ResPayload.Success(new { apps = apps ?? new List<AppEntry>() });
            }

            // -------- Расписания --------

            case Commands.ScheduleList:
                return ResPayload.Success(new { schedules = _schedules.List() });

            case Commands.ScheduleSave:
            {
                var schedule = GetObject<Schedule>(args, "schedule")
                               ?? throw new ArgumentException("Не передано расписание.");
                if (!ScheduleActions.All.Contains(schedule.Action))
                    throw new ArgumentException($"Неизвестное действие расписания: {schedule.Action}");

                var saved = await _schedules.SaveAsync(schedule, ct).ConfigureAwait(false);
                return ResPayload.Success(new { id = saved.Id, synced = saved.IsSynced });
            }

            case Commands.ScheduleDelete:
            {
                var id = GetString(args, "id") ?? throw new ArgumentException("Не указан id расписания.");
                return ResPayload.Success(new { deleted = await _schedules.DeleteAsync(id, ct).ConfigureAwait(false) });
            }

            case Commands.ScheduleSync:
            {
                var (synced, failed) = await _schedules.SyncAllAsync(ct).ConfigureAwait(false);
                return ResPayload.Success(new { synced, failed });
            }

            // -------- «Буду через N минут» --------

            case Commands.ArriveSet:
            {
                var minutes = GetInt(args, "minutes") ?? throw new ArgumentException("Не указано число минут.");
                minutes = Math.Clamp(minutes, 1, 24 * 60);
                var scenarioId = GetString(args, "scenarioId");
                var wakeAt = DateTime.Now.AddMinutes(minutes);

                var ok = await _wakeTimers.SetArriveTimerAsync(wakeAt, scenarioId, ct).ConfigureAwait(false);
                if (!ok)
                    return ResPayload.Failure(ErrorCodes.Os, "Не удалось создать таймер пробуждения.");

                _store.AppendEvent("arrive.set", $"Компьютер проснётся в {wakeAt:HH:mm}" +
                                                 (scenarioId is null ? "" : $" и выполнит сценарий {scenarioId}"));
                return ResPayload.Success(new { wakeAt = TimeUtil.Iso(wakeAt) });
            }

            case Commands.ArriveCancel:
                return ResPayload.Success(new { cancelled = await _wakeTimers.CancelArriveTimerAsync(ct).ConfigureAwait(false) });

            // -------- Мастер готовности --------

            case Commands.WizardRun:
            {
                var ids = GetStringArray(args, "ids");
                var checks = await _readiness.RunAsync(ids, ct).ConfigureAwait(false);
                await _bus.PublishAsync(Events.WizardResult, new { checks }, ct).ConfigureAwait(false);
                return ResPayload.Success(new { checks });
            }

            case Commands.WizardFix:
            {
                var id = GetString(args, "id") ?? throw new ArgumentException("Не указан id проверки.");
                var check = await _readiness.FixAsync(id, ct).ConfigureAwait(false);
                return ResPayload.Success(new { check });
            }

            case Commands.WakeTest:
            {
                var mode = GetString(args, "mode") ?? WakeTestService.ModeWol;
                var seconds = GetInt(args, "sleepSeconds") ?? 120;
                var test = await _wakeTest.StartAsync(mode, seconds, ct).ConfigureAwait(false);
                return ResPayload.Success(new { testId = test.TestId, sleepSeconds = test.SleepSeconds });
            }

            // -------- Сопряжение --------

            case Commands.PairRevoke:
            {
                var phoneId = GetString(args, "phoneId") ?? throw new ArgumentException("Не указан phoneId.");
                return ResPayload.Success(new { revoked = _pairing.Revoke(phoneId) });
            }

            case Commands.PairOffer:
            {
                // Новый телефон добавляют только с самого компьютера — иначе
                // сопряжённый телефон мог бы втихую пригласить ещё один.
                if (origin == CommandOrigin.Phone)
                    return ResPayload.Failure(ErrorCodes.Denied,
                        "Код сопряжения можно создать только на самом компьютере.");

                var offer = _pairing.CreateOffer();
                return ResPayload.Success(new { offer, uri = offer.ToUri() });
            }

            case Commands.PairList:
                return ResPayload.Success(new { phones = _pairing.Phones });

            // -------- Настройки --------

            case Commands.SettingsGet:
                return ResPayload.Success(new { settings = _store.Settings });

            case Commands.SettingsSet:
            {
                if (origin == CommandOrigin.Phone)
                {
                    // Опасные переключатели меняются только на самом компьютере.
                    var incoming = GetObject<AgentSettings>(args, "settings");
                    if (incoming is not null &&
                        (incoming.AllowShellSteps != _store.Settings.AllowShellSteps ||
                         incoming.AllowAutoLogon != _store.Settings.AllowAutoLogon))
                        return ResPayload.Failure(ErrorCodes.Denied,
                            "Выполнение произвольных команд и автовход включаются только на самом компьютере.");
                }

                var settings = GetObject<AgentSettings>(args, "settings")
                               ?? throw new ArgumentException("Не переданы настройки.");
                var saved = _store.SaveSettings(Merge(_store.Settings, settings, origin));
                return ResPayload.Success(new { settings = saved });
            }

            default:
                return ResPayload.Failure(ErrorCodes.UnknownCommand, $"Неизвестная команда: {cmd.Cmd}");
        }
    }

    private ResPayload PowerResponse(string action, JsonNode? args)
    {
        var delay = GetInt(args, "delaySec");
        var force = GetBool(args, "force") ?? false;
        var at = _power.Request(action, delay, force);
        return ResPayload.Success(new { scheduledAt = TimeUtil.Iso(at) });
    }

    private static AgentSettings Merge(AgentSettings current, AgentSettings incoming, CommandOrigin origin)
    {
        var merged = current.Clone();
        merged.CountdownSec = Math.Clamp(incoming.CountdownSec, 0, 600);
        merged.RelayUrl = incoming.RelayUrl ?? merged.RelayUrl;
        merged.LanPort = incoming.LanPort is >= 1024 and <= 65535 ? incoming.LanPort : merged.LanPort;
        merged.LanEnabled = incoming.LanEnabled;
        merged.HeartbeatSec = Math.Clamp(incoming.HeartbeatSec, 10, 300);
        merged.Language = incoming.Language ?? merged.Language;
        merged.WakeTimersOnBattery = incoming.WakeTimersOnBattery;
        merged.LogLevel = incoming.LogLevel ?? merged.LogLevel;

        if (origin != CommandOrigin.Phone)
        {
            merged.AllowShellSteps = incoming.AllowShellSteps;
            merged.AllowAutoLogon = incoming.AllowAutoLogon;
        }

        return merged;
    }

    // -------- Разбор аргументов --------

    // ToString() у JsonNode не бросает исключений и возвращает строку без кавычек,
    // поэтому кривой аргумент превращается в мусорную строку, а не в падение.
    private static string? GetString(JsonNode? args, string name) => args?[name]?.ToString();

    private static int? GetInt(JsonNode? args, string name)
    {
        var node = args?[name];
        if (node is null) return null;
        return int.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static bool? GetBool(JsonNode? args, string name)
    {
        var node = args?[name];
        if (node is null) return null;
        return bool.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static string[]? GetStringArray(JsonNode? args, string name)
    {
        if (args?[name] is not JsonArray array) return null;
        return array.Where(n => n is not null).Select(n => n!.ToString()).ToArray();
    }

    private static T? GetObject<T>(JsonNode? args, string name) where T : class
    {
        var node = args?[name];
        return node is null ? null : PcMateJson.Deserialize<T>(node.ToJsonString());
    }
}
