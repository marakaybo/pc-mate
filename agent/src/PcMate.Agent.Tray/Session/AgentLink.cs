using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using PcMate.Agent.Tray.Interop;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Util;

namespace PcMate.Agent.Tray.Session;

/// <summary>
/// Связь помощника со службой по именованному каналу. Автоматически
/// переподключается: служба может перезапуститься, а трей должен это пережить.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentLink : IAsyncDisposable
{
    private readonly StepExecutor _steps = new();
    private readonly AppInventory _apps = new();
    private readonly CancellationTokenSource _cts = new();

    private PipeLink? _link;
    private Task? _loop;

    public bool IsConnected => _link?.IsConnected == true;

    public event Action<bool>? ConnectionChanged;
    public event Action<CountdownRequest>? CountdownRequested;
    public event Action? CountdownDismissed;
    public event Action<NotifyRequest>? NotifyRequested;
    public event Action<StatusSnapshot>? StatusReceived;

    public void Start() => _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", "pcmate-session", PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);

                var link = new PipeLink(pipe, HandleAsync);
                _link = link;
                ConnectionChanged?.Invoke(true);
                delay = TimeSpan.FromSeconds(1);

                await link.NotifyAsync(IpcKinds.Hello, new IpcHello
                {
                    UserName = Environment.UserName,
                    SessionId = NativeUi.CurrentSessionId(),
                    Version = typeof(AgentLink).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
                }, ct).ConfigureAwait(false);

                await link.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Служба ещё не поднялась или перезапускается — ждём и пробуем снова.
            }
            finally
            {
                if (_link is not null)
                {
                    await _link.DisposeAsync().ConfigureAwait(false);
                    _link = null;
                    ConnectionChanged?.Invoke(false);
                }
            }

            if (ct.IsCancellationRequested) break;
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 1.6));
        }
    }

    private async Task<object?> HandleAsync(IpcMessage msg)
    {
        switch (msg.Kind)
        {
            case IpcKinds.StepExec:
            {
                var step = msg.DataAs<ScenarioStep>() ?? throw new InvalidOperationException("Пустой шаг.");
                return await _steps.ExecuteAsync(step).ConfigureAwait(false);
            }

            case IpcKinds.CountdownShow:
            {
                var request = msg.DataAs<CountdownRequest>();
                if (request is not null) CountdownRequested?.Invoke(request);
                return new { ok = true };
            }

            case IpcKinds.CountdownHide:
                CountdownDismissed?.Invoke();
                return new { ok = true };

            case IpcKinds.Notify:
            {
                var request = msg.DataAs<NotifyRequest>();
                if (request is not null) NotifyRequested?.Invoke(request);
                return new { ok = true };
            }

            case IpcKinds.AppsList:
            {
                var refresh = msg.Data?["refresh"]?.GetValue<bool>() ?? false;
                return _apps.Get(refresh);
            }

            case IpcKinds.ActiveWindow:
                return NativeUi.GetActiveWindowTitle();

            case IpcKinds.Lock:
            {
                if (!NativeUi.LockWorkStation())
                    throw new InvalidOperationException("Windows отказалась блокировать рабочую станцию.");
                return new { ok = true };
            }

            case IpcKinds.StatusPush:
            {
                var status = msg.DataAs<StatusSnapshot>();
                if (status is not null) StatusReceived?.Invoke(status);
                return new { ok = true };
            }

            case IpcKinds.Ping:
                return new { ok = true, at = TimeUtil.IsoNow() };

            default:
                return null;
        }
    }

    /// <summary>Выполняет команду протокола через службу (кнопки в трее).</summary>
    public async Task<JsonNode?> RunCommandAsync(string cmd, object? args = null, TimeSpan? timeout = null)
    {
        var link = _link ?? throw new InvalidOperationException(
            "Служба PC MATE недоступна. Проверьте, что она запущена (services.msc → PcMateAgent).");

        var reply = await link.RequestAsync(IpcKinds.CommandRun, new AgentCommandRequest
        {
            Cmd = cmd,
            Args = args is null ? null : JsonSerializer.SerializeToNode(args, PcMateJson.Options)
        }, timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Служба вернула ошибку.");
        return reply.Data;
    }

    public async Task<T?> RunCommandAsync<T>(string cmd, object? args = null, TimeSpan? timeout = null)
        where T : class
    {
        var data = await RunCommandAsync(cmd, args, timeout).ConfigureAwait(false);
        return data is null ? null : PcMateJson.Deserialize<T>(data.ToJsonString());
    }

    public async Task ReportCountdownCancelledAsync()
    {
        var link = _link;
        if (link is null) return;
        await link.NotifyAsync(IpcKinds.CountdownCancelled).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_link is not null) await _link.DisposeAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* остановлен */ }
        }
        _cts.Dispose();
    }
}
