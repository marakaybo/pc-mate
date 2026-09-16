using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Services;
using PcMate.Core.Ipc;
using PcMate.Core.Util;
using PcMate.Agent.Service.Power;

namespace PcMate.Agent.Service.Ipc;

/// <summary>
/// Служба работает в Session 0 и не может открывать окна на рабочем столе.
/// Здесь живёт сервер именованного канала, к которому подключается помощник
/// из сеанса пользователя (трей). Через него идут запуск программ, окно
/// обратного отсчёта, уведомления и блокировка.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionHost : BackgroundService
{
    public const string PipeName = "pcmate-session";

    private readonly ILogger<SessionHost> _log;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, SessionClient> _clients = new();

    public SessionHost(ILogger<SessionHost> log, IServiceProvider services)
    {
        _log = log;
        _services = services;
    }

    public sealed record SessionClient(string Id, PipeLink Link, IpcHello Hello);

    public bool HasUserSession => _clients.Values.Any(c => c.Link.IsConnected);

    public IpcHello? ActiveUser => PickClient()?.Hello;

    /// <summary>Клиент в активном консольном сеансе; иначе — любой подключённый.</summary>
    private SessionClient? PickClient()
    {
        var alive = _clients.Values.Where(c => c.Link.IsConnected).ToList();
        if (alive.Count == 0) return null;

        var consoleSession = (int)NativeMethods.WTSGetActiveConsoleSessionId();
        return alive.FirstOrDefault(c => c.Hello.SessionId == consoleSession) ?? alive[0];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Сервер сеансового канала запущен: \\\\.\\pipe\\{Pipe}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _log.LogError(ex, "Ошибка сеансового канала, повтор через 2 с.");
                await Task.Delay(2000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        // Помощник работает от обычного пользователя — ему нужен доступ на чтение и запись.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        var hello = new IpcHello();
        PipeLink? link = null;

        try
        {
            link = new PipeLink(server, msg => HandleFromTrayAsync(clientId, msg, hello));
            _clients[clientId] = new SessionClient(clientId, link, hello);
            await link.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Сеансовый клиент {Id} отключился с ошибкой", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            if (link is not null) await link.DisposeAsync().ConfigureAwait(false);
            else await server.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation("Помощник отключён ({Id}).", clientId);
        }
    }

    private async Task<object?> HandleFromTrayAsync(string clientId, IpcMessage msg, IpcHello hello)
    {
        switch (msg.Kind)
        {
            case IpcKinds.Hello:
            {
                var payload = msg.DataAs<IpcHello>() ?? new IpcHello();
                hello.UserName = payload.UserName;
                hello.SessionId = payload.SessionId;
                hello.Version = payload.Version;
                _log.LogInformation("Помощник подключён: {User} (сеанс {Session}, версия {Version})",
                    hello.UserName, hello.SessionId, hello.Version);
                return new { ok = true };
            }

            case IpcKinds.CountdownCancelled:
            {
                var power = _services.GetService<PowerController>();
                power?.Cancel("отменено на компьютере");
                return new { ok = true };
            }

            case IpcKinds.CommandRun:
            {
                var request = msg.DataAs<AgentCommandRequest>();
                if (request is null) throw new InvalidOperationException("Пустой запрос команды.");

                var local = _services.GetRequiredService<LocalCommandGateway>();
                return await local.ExecuteAsync(request).ConfigureAwait(false);
            }

            default:
                return null;
        }
    }

    public async Task<T?> RequestAsync<T>(string kind, object? data = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var client = PickClient() ?? throw new InvalidOperationException(
            "Нет активного сеанса пользователя: помощник PC MATE не подключён.");
        return await client.Link.RequestAsync<T>(kind, data, timeout, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryNotifyAsync(string kind, object? data = null, CancellationToken ct = default)
    {
        var client = PickClient();
        if (client is null) return false;
        try
        {
            await client.Link.NotifyAsync(kind, data, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Не удалось отправить {Kind} помощнику", kind);
            return false;
        }
    }

    /// <summary>Рассылает всем подключённым помощникам (например, обновление статуса в трее).</summary>
    public async Task BroadcastAsync(string kind, object? data = null, CancellationToken ct = default)
    {
        foreach (var client in _clients.Values.Where(c => c.Link.IsConnected))
        {
            try { await client.Link.NotifyAsync(kind, data, ct).ConfigureAwait(false); }
            catch { /* клиент уходит — снимется в HandleClientAsync */ }
        }
    }
}

/// <summary>
/// Позволяет помощнику выполнять команды протокола локально (кнопки в трее),
/// минуя сеть, но проходя через тот же диспетчер, что и команды с телефона.
/// </summary>
public sealed class LocalCommandGateway
{
    private readonly IServiceProvider _services;

    public LocalCommandGateway(IServiceProvider services) => _services = services;

    public async Task<JsonElement?> ExecuteAsync(AgentCommandRequest request, CancellationToken ct = default)
    {
        var dispatcher = _services.GetRequiredService<CommandDispatcher>();
        var payload = new PcMate.Core.Protocol.CmdPayload { Cmd = request.Cmd, Args = request.Args };
        var res = await dispatcher.ExecuteAsync(payload, CommandOrigin.Local, ct).ConfigureAwait(false);

        if (!res.Ok)
            throw new InvalidOperationException(res.Error?.Message ?? "Команда не выполнена.");

        return res.Result is null ? null : JsonSerializer.SerializeToElement(res.Result, PcMateJson.Options);
    }
}
