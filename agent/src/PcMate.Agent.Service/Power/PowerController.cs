using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Ipc;
using PcMate.Agent.Service.Services;
using PcMate.Agent.Service.State;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Power;

/// <summary>
/// Выполнение команд питания с обратным отсчётом и возможностью отмены.
/// Отсчёт виден и на ПК (окно помощника), и на телефоне (события power.countdown).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerController
{
    private readonly ILogger<PowerController> _log;
    private readonly AgentEventBus _bus;
    private readonly SessionHost _session;
    private readonly AgentStore _store;

    private readonly object _gate = new();
    private CancellationTokenSource? _pendingCts;
    private PendingPowerInfo? _pending;

    public PowerController(ILogger<PowerController> log, AgentEventBus bus, SessionHost session, AgentStore store)
    {
        _log = log;
        _bus = bus;
        _session = session;
        _store = store;
    }

    public PendingPowerInfo? Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>Ставит действие питания в очередь с обратным отсчётом. Возвращает момент выполнения.</summary>
    public DateTimeOffset Request(string action, int? delaySec = null, bool force = false, string source = "phone")
    {
        if (!PowerActions.All.Contains(action))
            throw new ArgumentException($"Неизвестное действие питания: {action}", nameof(action));

        var delay = Math.Clamp(delaySec ?? _store.Settings.CountdownSec, 0, 3600);

        // Блокировка экрана мгновенна и безобидна — без отсчёта.
        if (action == PowerActions.Lock) delay = 0;

        lock (_gate)
        {
            _pendingCts?.Cancel();
            _pendingCts = new CancellationTokenSource();
            _pending = new PendingPowerInfo { Action = action, SecondsLeft = delay, Cancellable = delay > 0 };
        }

        var runAt = DateTimeOffset.Now.AddSeconds(delay);
        var cts = _pendingCts!;
        _ = RunCountdownAsync(action, delay, force, source, cts.Token);
        return runAt;
    }

    public bool Cancel(string reason = "отменено с телефона")
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _pendingCts;
            _pendingCts = null;
            if (_pending is null) return false;
            _pending = null;
        }

        cts?.Cancel();
        _log.LogInformation("Действие питания отменено: {Reason}", reason);
        _store.AppendEvent("power.cancel", $"Действие питания отменено ({reason})");
        _ = _session.TryNotifyAsync(IpcKinds.CountdownHide);
        _bus.Publish(Events.PowerCountdown, new { action = "", secondsLeft = 0, cancelled = true, reason });
        return true;
    }

    private async Task RunCountdownAsync(string action, int seconds, bool force, string source, CancellationToken ct)
    {
        try
        {
            if (seconds > 0)
            {
                _log.LogInformation("{Action}: отсчёт {Seconds} с (источник: {Source})", action, seconds, source);

                await _session.TryNotifyAsync(IpcKinds.CountdownShow, new CountdownRequest
                {
                    Action = action,
                    ActionTitle = PowerActions.RussianTitle(action),
                    Seconds = seconds,
                    Cancellable = true
                }, ct).ConfigureAwait(false);

                for (var left = seconds; left > 0; left--)
                {
                    lock (_gate)
                        if (_pending is not null) _pending.SecondsLeft = left;

                    // Телефону шлём в начале, затем раз в 5 секунд и в последние 5 секунд.
                    if (left == seconds || left % 5 == 0 || left <= 5)
                        await _bus.PublishAsync(Events.PowerCountdown,
                            new { action, secondsLeft = left, cancellable = true }, ct).ConfigureAwait(false);

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _pending = null;
                _pendingCts = null;
            }

            await ExecuteAsync(action, force).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отменено пользователем — состояние уже сброшено в Cancel().
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось выполнить действие питания {Action}", action);
            _store.AppendEvent("power.error", $"Ошибка выполнения «{PowerActions.RussianTitle(action)}»: {ex.Message}");
            await _bus.PublishAsync(Events.Notify,
                new { text = $"Не удалось выполнить: {PowerActions.RussianTitle(action)}", level = "error" })
                .ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(string action, bool force)
    {
        _log.LogInformation("Выполняется действие питания: {Action} (force={Force})", action, force);

        switch (action)
        {
            case PowerActions.Lock:
                await _session.RequestAsync<object>(IpcKinds.Lock, timeout: TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                _store.AppendEvent("power.lock", "Компьютер заблокирован");
                await _bus.PublishAsync(Events.PowerState, new { state = PowerStates.Locked }).ConfigureAwait(false);
                return;

            case PowerActions.Sleep:
            case PowerActions.Hibernate:
            {
                var hibernate = action == PowerActions.Hibernate;
                var state = hibernate ? PowerStates.Hibernating : PowerStates.Sleeping;

                _store.AppendEvent("power." + action, PowerActions.RussianTitle(action));
                await _bus.PublishAsync(Events.PowerState, new { state, reason = "по команде" }).ConfigureAwait(false);
                // Даём транспортам мгновение, чтобы отправить событие до засыпания.
                await Task.Delay(700).ConfigureAwait(false);

                EnableShutdownPrivilege();
                if (!NativeMethods.SetSuspendState(hibernate, force, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        hibernate
                            ? "Не удалось перейти в гибернацию. Проверьте, что она включена (powercfg /h on)."
                            : "Не удалось перейти в спящий режим.");
                return;
            }

            case PowerActions.Shutdown:
            case PowerActions.Reboot:
            {
                var reboot = action == PowerActions.Reboot;

                _store.AppendEvent("power." + action, PowerActions.RussianTitle(action));
                await _bus.PublishAsync(Events.PowerState,
                    new { state = PowerStates.ShuttingDown, reason = reboot ? "перезагрузка" : "выключение" })
                    .ConfigureAwait(false);
                await Task.Delay(700).ConfigureAwait(false);

                EnableShutdownPrivilege();
                if (!NativeMethods.InitiateSystemShutdownEx(null, null, 0, force, reboot,
                        NativeMethods.ShutdownReasonPlanned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось инициировать выключение.");
                return;
            }

            default:
                throw new ArgumentException($"Неизвестное действие питания: {action}", nameof(action));
        }
    }

    /// <summary>Отменяет системный shutdown, инициированный не нами (на всякий случай).</summary>
    public bool AbortSystemShutdown()
    {
        try
        {
            EnableShutdownPrivilege();
            return NativeMethods.AbortSystemShutdown(null);
        }
        catch
        {
            return false;
        }
    }

    private static void EnableShutdownPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть токен процесса.");

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, NativeMethods.SeShutdownName, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не найдена привилегия SeShutdownPrivilege.");

            var tp = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled
                }
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref tp,
                    (uint)Marshal.SizeOf<NativeMethods.TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось включить привилегию выключения.");

            var err = Marshal.GetLastWin32Error();
            if (err != 0)
                throw new Win32Exception(err, "Привилегия выключения недоступна для этой учётной записи.");
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
