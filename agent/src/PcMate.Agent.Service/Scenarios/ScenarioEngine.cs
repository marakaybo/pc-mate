using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Ipc;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Services;
using PcMate.Agent.Service.State;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Scenarios;

/// <summary>
/// Исполнитель сценариев. Шаги, которым нужен рабочий стол (запуск программ, сайты,
/// громкость), уходят помощнику в сеансе пользователя; остальные выполняются службой.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScenarioEngine
{
    private readonly ILogger<ScenarioEngine> _log;
    private readonly AgentStore _store;
    private readonly SessionHost _session;
    private readonly AgentEventBus _bus;
    private readonly PowerController _power;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public ScenarioEngine(ILogger<ScenarioEngine> log, AgentStore store, SessionHost session,
        AgentEventBus bus, PowerController power)
    {
        _log = log;
        _store = store;
        _session = session;
        _bus = bus;
        _power = power;
    }

    public IReadOnlyCollection<string> RunningIds => _running.Keys.ToList();

    /// <summary>Запускает сценарий в фоне и сразу возвращает идентификатор запуска.</summary>
    public string Start(Scenario scenario, string source = "phone")
    {
        var report = new ScenarioRunReport
        {
            ScenarioId = scenario.Id,
            Name = scenario.Name
        };

        var cts = new CancellationTokenSource();
        _running[report.RunId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(scenario, report, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Сценарий {Name} завершился ошибкой", scenario.Name);
            }
            finally
            {
                _running.TryRemove(report.RunId, out var done);
                done?.Dispose();
            }
        });

        _log.LogInformation("Запуск сценария «{Name}» ({RunId}), источник {Source}", scenario.Name, report.RunId, source);
        return report.RunId;
    }

    public bool Cancel(string runId)
    {
        if (!_running.TryGetValue(runId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    private async Task RunAsync(Scenario scenario, ScenarioRunReport report, CancellationToken ct)
    {
        var steps = scenario.Steps.Where(s => s.Enabled).ToList();

        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            var sw = Stopwatch.StartNew();

            var result = new StepResult
            {
                Index = i,
                Type = step.Type,
                Title = step.DisplayTitle
            };

            try
            {
                var message = await ExecuteStepAsync(step, ct).ConfigureAwait(false);
                result.Status = "ok";
                result.Message = message;
            }
            catch (OperationCanceledException)
            {
                result.Status = "skipped";
                result.Message = "Сценарий остановлен";
                result.DurationMs = sw.ElapsedMilliseconds;
                report.Steps.Add(result);
                break;
            }
            catch (Exception ex)
            {
                result.Status = "error";
                result.Message = ex.Message;
                _log.LogWarning(ex, "Шаг {Index} ({Type}) сценария «{Name}» не выполнен", i, step.Type, scenario.Name);
            }

            result.DurationMs = sw.ElapsedMilliseconds;
            report.Steps.Add(result);

            await _bus.PublishAsync(Events.ScenarioProgress, new
            {
                runId = report.RunId,
                id = scenario.Id,
                stepIndex = i,
                total = steps.Count,
                step,
                status = result.Status,
                message = result.Message
            }, ct).ConfigureAwait(false);

            var stopOnThisError = step.ContinueOnError == false || (step.ContinueOnError is null && scenario.StopOnError);
            if (result.Status == "error" && stopOnThisError)
            {
                // Остальные шаги помечаем пропущенными, чтобы отчёт был полным.
                for (var j = i + 1; j < steps.Count; j++)
                    report.Steps.Add(new StepResult
                    {
                        Index = j,
                        Type = steps[j].Type,
                        Title = steps[j].DisplayTitle,
                        Status = "skipped",
                        Message = "Пропущен из-за ошибки предыдущего шага"
                    });
                break;
            }
        }

        report.Ok = report.Steps.All(s => s.Status != "error");
        report.FinishedAt = TimeUtil.IsoNow();

        var okCount = report.Steps.Count(s => s.Status == "ok");
        _store.AppendEvent("scenario.done",
            $"Сценарий «{scenario.Name}»: {okCount} из {report.Steps.Count} шагов выполнено");

        await _bus.PublishAsync(Events.ScenarioDone, report, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string?> ExecuteStepAsync(ScenarioStep step, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSec <= 0 ? 60 : step.TimeoutSec, 1, 3600));

        switch (step.Type)
        {
            case StepTypes.Wait:
            {
                var seconds = Math.Clamp(step.Seconds, 0, 3600);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                return $"Пауза {seconds:0.#} с";
            }

            case StepTypes.Notify:
            {
                await _bus.PublishAsync(Events.Notify, new
                {
                    text = step.Text ?? "",
                    level = step.NotifyLevel ?? "info"
                }, ct).ConfigureAwait(false);
                return "Уведомление отправлено";
            }

            case StepTypes.Power:
            {
                var action = step.Action ?? PowerActions.Sleep;
                if (!PowerActions.All.Contains(action))
                    throw new InvalidOperationException($"Неизвестное действие питания: {action}");
                _power.Request(action, step.DelaySec, force: false, source: "scenario");
                return $"Запланировано: {PowerActions.RussianTitle(action)}";
            }

            case StepTypes.Command:
            {
                if (!_store.Settings.AllowShellSteps)
                    throw new InvalidOperationException(
                        "Выполнение произвольных команд выключено. Включите его в настройках агента на самом компьютере.");

                if (string.IsNullOrWhiteSpace(step.Command))
                    throw new InvalidOperationException("Пустая команда.");

                var isPowerShell = string.Equals(step.Shell, "powershell", StringComparison.OrdinalIgnoreCase);
                var res = isPowerShell
                    ? await ProcessRunner.PowerShellAsync(step.Command!, timeout, ct).ConfigureAwait(false)
                    : await ProcessRunner.RunAsync("cmd.exe", "/c " + step.Command, timeout, ct).ConfigureAwait(false);

                if (!res.Ok)
                    throw new InvalidOperationException($"Команда вернула код {res.ExitCode}: {Trim(res.All)}");
                return Trim(res.StdOut);
            }

            default:
            {
                if (!step.NeedsUserSession)
                    throw new InvalidOperationException($"Неизвестный тип шага: {step.Type}");

                if (!_session.HasUserSession)
                    throw new InvalidOperationException(
                        "Нет активного сеанса пользователя: программы запускать некуда. " +
                        "После гибернации сеанс сохраняется; после полного выключения нужен вход в Windows.");

                var result = await _session
                    .RequestAsync<StepExecResult>(IpcKinds.StepExec, step, timeout, ct)
                    .ConfigureAwait(false);

                if (result is null) return null;
                if (!result.Ok) throw new InvalidOperationException(result.Message ?? "Шаг не выполнен.");
                return result.Message;
            }
        }
    }

    private static string Trim(string value)
    {
        var text = value.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
