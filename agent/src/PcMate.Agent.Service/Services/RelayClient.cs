using System.Text.Json;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Net;
using PcMate.Agent.Service.Pairing;
using PcMate.Agent.Service.State;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

/// <summary>
/// Связь с сервером-ретранслятором. Исходящее соединение — на домашнем роутере
/// не нужно открывать ни одного порта. Сервер видит только маршрутизацию:
/// содержимое команд зашифровано ключами телефона и ПК.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RelayClient : BackgroundService
{
    private readonly ILogger<RelayClient> _log;
    private readonly AgentStore _store;
    private readonly PairingService _pairing;
    private readonly EnvelopeProcessor _processor;
    private readonly AgentEventBus _bus;
    private readonly StatusProvider _status;
    private readonly RelayStatus _relayStatus;
    private readonly ScheduleService _schedules;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;

    public RelayClient(ILogger<RelayClient> log, AgentStore store, PairingService pairing,
        EnvelopeProcessor processor, AgentEventBus bus, StatusProvider status, RelayStatus relayStatus,
        ScheduleService schedules)
    {
        _log = log;
        _store = store;
        _pairing = pairing;
        _processor = processor;
        _bus = bus;
        _status = status;
        _relayStatus = relayStatus;
        _schedules = schedules;
    }

    public bool Connected => _socket?.State == WebSocketState.Open;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.Subscribe(ForwardEventAsync);
        _pairing.OfferCreated += OnOfferCreated;
        var delay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            var relayUrl = _store.Settings.RelayUrl;
            if (string.IsNullOrWhiteSpace(relayUrl))
            {
                _relayStatus.MarkDisabled();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ConnectAndRunAsync(relayUrl, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2); // успешное соединение сбрасывает задержку
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _relayStatus.MarkDisconnected(ex.Message);
                _log.LogWarning("Нет связи с сервером ({Message}). Повтор через {Delay:0} с.",
                    ex.Message, delay.TotalSeconds);
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            // Экспоненциальная задержка до 60 секунд (ТЗ §6.2).
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }

        _pairing.OfferCreated -= OnOfferCreated;
    }

    /// <summary>Публикует одноразовый код на сервере, чтобы телефон мог завершить сопряжение из любой сети.</summary>
    private void OnOfferCreated(PairingOffer offer)
    {
        if (!Connected) return;
        _ = SendSysAsync(SysMessages.PairOffer, new { token = offer.Token, exp = offer.Exp });
    }

    private async Task ConnectAndRunAsync(string relayUrl, CancellationToken ct)
    {
        var token = _store.GetDeviceToken() ?? await RegisterAsync(relayUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Не удалось зарегистрироваться на сервере.");

        var wsUrl = BuildWebSocketUrl(relayUrl);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);

        _log.LogInformation("Подключение к серверу {Url}", wsUrl);
        await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
        _socket = socket;

        var adapter = NetworkProbe.GetPrimaryAdapter();
        await SendSysAsync(SysMessages.Hello, new
        {
            role = DeviceRoles.Pc,
            token,
            version = _status.AgentVersion,
            name = _store.Identity.PcName,
            mac = adapter?.Mac,
            lanIps = NetworkProbe.LocalIps()
        }, ct).ConfigureAwait(false);

        _relayStatus.MarkConnected(relayUrl);
        _store.AppendEvent("relay.connect", "Установлена связь с сервером");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatLoopAsync(heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* остановлен */ }
            _socket = null;
            _relayStatus.MarkDisconnected();
            _store.AppendEvent("relay.disconnect", "Связь с сервером потеряна");
        }
    }

    private async Task<string?> RegisterAsync(string relayUrl, CancellationToken ct)
    {
        var httpBase = ToHttpBase(relayUrl);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var adapter = NetworkProbe.GetPrimaryAdapter();

        var response = await http.PostAsJsonAsync($"{httpBase}/api/v1/devices/register", new
        {
            deviceId = _store.Identity.DeviceId,
            role = DeviceRoles.Pc,
            name = _store.Identity.PcName,
            pub = _store.Identity.PublicKey,
            mac = adapter?.Mac,
            lanIps = NetworkProbe.LocalIps()
        }, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Сервер отклонил регистрацию ({(int)response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json);
        var token = node?["deviceToken"]?.ToString();

        if (!string.IsNullOrEmpty(token))
        {
            _store.SaveDeviceToken(relayUrl, token!);
            _log.LogInformation("Агент зарегистрирован на сервере {Url}", relayUrl);
        }

        return token;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var builder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var text = builder.ToString();
            builder.Clear();

            if (!PcMateJson.TryDeserialize<Envelope>(text, out var envelope) || envelope is null) continue;

            try
            {
                if (envelope.Type == MessageType.Sys)
                    await HandleSysAsync(envelope, ct).ConfigureAwait(false);
                else
                {
                    var response = await _processor.HandleAsync(envelope, ct).ConfigureAwait(false);
                    if (response is not null) await SendEnvelopeAsync(response, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка обработки сообщения с сервера");
            }
        }
    }

    private async Task HandleSysAsync(Envelope envelope, CancellationToken ct)
    {
        switch (envelope.Sys)
        {
            case SysMessages.Ping:
                await SendSysAsync(SysMessages.Pong, new { t = TimeUtil.UnixNow() }, ct).ConfigureAwait(false);
                break;

            case SysMessages.HelloOk:
                _log.LogInformation("Сервер подтвердил подключение.");
                break;

            case SysMessages.PairClaim:
            {
                var claim = envelope.DataAs<PairClaim>();
                if (claim is null) return;

                var result = _pairing.Claim(claim);
                await SendSysAsync(SysMessages.PairResult, new
                {
                    pairId = result.PairId,
                    ok = result.Ok,
                    pcId = result.PcId,
                    pcPub = result.PcPub,
                    pcName = result.PcName,
                    mac = result.Mac,
                    lanIps = result.LanIps,
                    fingerprint = result.Fingerprint,
                    error = result.Error
                }, ct).ConfigureAwait(false);
                break;
            }

            case SysMessages.SchedulePush:
            {
                var schedules = envelope.Data?["schedules"]?.Deserialize<List<Schedule>>(PcMateJson.Options);
                if (schedules is null) return;

                _log.LogInformation("Сервер прислал {Count} расписаний — синхронизируем таймеры.", schedules.Count);
                foreach (var schedule in schedules)
                    await _schedules.SaveAsync(schedule, ct).ConfigureAwait(false);
                break;
            }

            case SysMessages.RouteFail:
            {
                var reason = envelope.Data?["reason"]?.ToString();
                _log.LogWarning("Сервер не смог доставить сообщение: {Reason}", reason);
                break;
            }

            case SysMessages.Error:
            {
                var code = envelope.Data?["code"]?.ToString();
                var message = envelope.Data?["message"]?.ToString();
                _log.LogError("Сервер сообщил об ошибке {Code}: {Message}", code, message);
                break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_store.Settings.HeartbeatSec, 10, 300));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await SendSysAsync(SysMessages.Heartbeat, new
                {
                    state = PowerStates.Running,
                    uptimeSec = (long)(Power.NativeMethods.GetTickCount64() / 1000)
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Heartbeat не отправлен");
                break;
            }
        }
    }

    private async Task ForwardEventAsync(EvtPayload evt, CancellationToken ct)
    {
        if (!Connected) return;

        foreach (var phone in _pairing.Phones)
        {
            var envelope = _processor.SealEventFor(phone.PhoneId, evt);
            if (envelope is null) continue;
            try
            {
                await SendEnvelopeAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Событие {Evt} не доставлено телефону {Phone}", evt.Evt, phone.PhoneId);
            }
        }
    }

    public Task SendSysAsync(string sys, object? data, CancellationToken ct = default)
        => SendEnvelopeAsync(Envelope.Sys_(_store.Identity.DeviceId, sys, data), ct);

    private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(PcMateJson.Serialize(envelope));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal static string BuildWebSocketUrl(string relayUrl)
    {
        var url = relayUrl.Trim().TrimEnd('/');
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "ws://" + url[7..];
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "wss://" + url[8..];
        else if (!url.StartsWith("ws", StringComparison.OrdinalIgnoreCase)) url = "wss://" + url;

        return url.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? url : url + "/ws";
    }

    internal static string ToHttpBase(string relayUrl)
    {
        var url = relayUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/ws", StringComparison.OrdinalIgnoreCase)) url = url[..^3];
        if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + url[6..];
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + url[5..];
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        return "https://" + url;
    }
}
