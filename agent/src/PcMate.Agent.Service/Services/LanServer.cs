using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Pairing;
using PcMate.Agent.Service.State;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

/// <summary>
/// Локальный сервер агента: сопряжение и управление прямо из домашней сети,
/// без сервера-ретранслятора и без интернета. Это режим первого этапа и
/// запасной путь, если сервер недоступен.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LanServer : BackgroundService
{
    private readonly ILogger<LanServer> _log;
    private readonly AgentStore _store;
    private readonly PairingService _pairing;
    private readonly EnvelopeProcessor _processor;
    private readonly AgentEventBus _bus;
    private readonly StatusProvider _status;

    private readonly ConcurrentDictionary<string, LanClient> _clients = new();
    private HttpListener? _listener;

    public LanServer(ILogger<LanServer> log, AgentStore store, PairingService pairing,
        EnvelopeProcessor processor, AgentEventBus bus, StatusProvider status)
    {
        _log = log;
        _store = store;
        _pairing = pairing;
        _processor = processor;
        _bus = bus;
        _status = status;
    }

    private sealed record LanClient(string PhoneId, WebSocket Socket, SemaphoreSlim SendLock);

    public int ConnectedClients => _clients.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_store.Settings.LanEnabled)
        {
            _log.LogInformation("Локальный сервер выключен в настройках.");
            return;
        }

        using var subscription = _bus.Subscribe(BroadcastEventAsync);
        var port = _store.Settings.LanPort;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();
                _log.LogInformation("Локальный сервер слушает порт {Port}", port);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                    _ = HandleRequestAsync(context, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                // Код 5 — «отказано в доступе»: под обычной учётной записью Windows
                // не даёт слушать порт. В рабочем режиме служба идёт от SYSTEM,
                // а вот при ручной отладке нужна консоль администратора.
                var hint = ex.ErrorCode == 5
                    ? $"Занять порт {port} может только служба (учётная запись SYSTEM) " +
                      "или процесс, запущенный от администратора. " +
                      $"Для отладки из консоли выполните: netsh http add urlacl url=http://+:{port}/ user={Environment.UserName}"
                    : $"Не удалось занять порт {port}. Проверьте, что он свободен и разрешён в брандмауэре.";

                _log.LogError(ex, "{Hint} Повтор через 15 с.", hint);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка локального сервера, перезапуск через 10 с.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                try { _listener?.Close(); } catch { /* уже закрыт */ }
                _listener = null;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";

        try
        {
            switch (path)
            {
                case "/health":
                case "":
                    await WriteJsonAsync(context, new
                    {
                        ok = true,
                        product = "PC MATE",
                        pcId = _store.Identity.DeviceId,
                        pcName = _store.Identity.PcName,
                        version = _status.AgentVersion,
                        protocol = Envelope.ProtocolVersion
                    }).ConfigureAwait(false);
                    return;

                case "/pair/claim":
                    await HandlePairClaimAsync(context).ConfigureAwait(false);
                    return;

                case "/status":
                    // Незашифрованный статус отдаём только сопряжённым телефонам по их id.
                    await HandleLanStatusAsync(context, ct).ConfigureAwait(false);
                    return;

                case "/ws":
                    await HandleWebSocketAsync(context, ct).ConfigureAwait(false);
                    return;

                default:
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context, new { error = "not_found" }).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка обработки запроса {Path}", path);
            try
            {
                context.Response.StatusCode = 500;
                await WriteJsonAsync(context, new { error = ex.Message }).ConfigureAwait(false);
            }
            catch { /* клиент уже ушёл */ }
        }
    }

    private async Task HandlePairClaimAsync(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 405;
            await WriteJsonAsync(context, new { error = "method_not_allowed" }).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        var claim = PcMateJson.Deserialize<PairClaim>(body);
        if (claim is null)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { ok = false, error = "Некорректный запрос сопряжения." }).ConfigureAwait(false);
            return;
        }

        var result = _pairing.Claim(claim);
        context.Response.StatusCode = result.Ok ? 200 : 403;
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private async Task HandleLanStatusAsync(HttpListenerContext context, CancellationToken ct)
    {
        var phoneId = context.Request.QueryString["phoneId"];
        if (string.IsNullOrWhiteSpace(phoneId) || !_pairing.TryGetKeys(phoneId!, out _))
        {
            context.Response.StatusCode = 403;
            await WriteJsonAsync(context, new { error = "not_paired" }).ConfigureAwait(false);
            return;
        }

        var status = await _status.GetAsync(includeActiveWindow: false, ct).ConfigureAwait(false);
        await WriteJsonAsync(context, status).ConfigureAwait(false);
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = "websocket_expected" }).ConfigureAwait(false);
            return;
        }

        var phoneId = context.Request.QueryString["phoneId"] ?? "";
        if (string.IsNullOrWhiteSpace(phoneId) || !_pairing.TryGetKeys(phoneId, out _))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            _log.LogWarning("Локальное подключение отклонено: телефон {PhoneId} не сопряжён", phoneId);
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var socket = wsContext.WebSocket;
        var client = new LanClient(phoneId, socket, new SemaphoreSlim(1, 1));
        _clients[phoneId] = client;

        _log.LogInformation("Телефон {PhoneId} подключился по локальной сети ({Address})",
            phoneId, context.Request.RemoteEndPoint?.Address);

        try
        {
            // Приветствие, чтобы телефон сразу увидел версию и имя ПК.
            var hello = _processor.SealEventFor(phoneId, EvtPayload.Of(Events.AgentHello, new
            {
                version = _status.AgentVersion,
                pcName = _store.Identity.PcName,
                transport = "lan"
            }));
            if (hello is not null) await SendAsync(client, hello, ct).ConfigureAwait(false);

            await ReceiveLoopAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Локальное соединение с {PhoneId} закрыто", phoneId);
        }
        finally
        {
            _clients.TryRemove(phoneId, out _);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { /* уже закрыт */ }
            socket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(LanClient client, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();

        while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await client.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var text = builder.ToString();
            builder.Clear();

            if (!PcMateJson.TryDeserialize<Envelope>(text, out var envelope) || envelope is null) continue;

            var response = await _processor.HandleAsync(envelope, ct).ConfigureAwait(false);
            if (response is not null) await SendAsync(client, response, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(LanClient client, Envelope envelope, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(PcMateJson.Serialize(envelope));
        await client.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private async Task BroadcastEventAsync(EvtPayload evt, CancellationToken ct)
    {
        foreach (var client in _clients.Values.ToList())
        {
            var envelope = _processor.SealEventFor(client.PhoneId, evt);
            if (envelope is null) continue;

            try
            {
                await SendAsync(client, envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Не удалось отправить событие телефону {PhoneId}", client.PhoneId);
                _clients.TryRemove(client.PhoneId, out _);
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(PcMateJson.Serialize(payload));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}
