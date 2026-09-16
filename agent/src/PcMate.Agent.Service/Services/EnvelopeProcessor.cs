using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Pairing;
using PcMate.Agent.Service.State;
using PcMate.Core.Crypto;
using PcMate.Core.Protocol;

namespace PcMate.Agent.Service.Services;

/// <summary>
/// Общая логика обработки конверта от телефона: расшифровать, выполнить, зашифровать ответ.
/// Одинакова и для локальной сети, и для сервера-ретранслятора.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnvelopeProcessor
{
    private readonly ILogger<EnvelopeProcessor> _log;
    private readonly PairingService _pairing;
    private readonly CommandDispatcher _dispatcher;
    private readonly AgentStore _store;
    private readonly MessageCodec _codec;

    public EnvelopeProcessor(ILogger<EnvelopeProcessor> log, PairingService pairing,
        CommandDispatcher dispatcher, AgentStore store)
    {
        _log = log;
        _pairing = pairing;
        _dispatcher = dispatcher;
        _store = store;
        _codec = new MessageCodec(store.Identity.DeviceId, new ReplayGuard());
    }

    public MessageCodec Codec => _codec;

    /// <summary>Обрабатывает входящий конверт и возвращает конверт-ответ (или null, если ответ не нужен).</summary>
    public async Task<Envelope?> HandleAsync(Envelope incoming, CancellationToken ct = default)
    {
        if (incoming.Type != MessageType.Cmd)
        {
            _log.LogDebug("Пропущен конверт типа {Type} от {From}", incoming.Type, incoming.From);
            return null;
        }

        if (!_pairing.TryGetKeys(incoming.From, out var keys))
        {
            _log.LogWarning("Команда от несопряжённого устройства {From}", incoming.From);
            return null; // молча игнорируем: отвечать нечем — общего ключа нет
        }

        var opened = _codec.Open<CmdPayload>(incoming, keys.ReceiveKey);
        if (!opened.Ok)
        {
            _log.LogWarning("Не удалось принять команду от {From}: {Code} {Message}",
                incoming.From, opened.ErrorCode, opened.ErrorMessage);
            _store.AppendEvent("cmd.reject", $"Отклонена команда от {incoming.From}: {opened.ErrorMessage}", "phone");

            var rejection = ResPayload.Failure(opened.ErrorCode!, opened.ErrorMessage!);
            return _codec.SealResponse(incoming.From, keys.SendKey, rejection, incoming.Id);
        }

        _pairing.TouchPhone(incoming.From);

        var response = await _dispatcher.ExecuteAsync(opened.Value!, CommandOrigin.Phone, ct).ConfigureAwait(false);
        return _codec.SealResponse(incoming.From, keys.SendKey, response, incoming.Id);
    }

    /// <summary>Готовит зашифрованное событие для конкретного телефона.</summary>
    public Envelope? SealEventFor(string phoneId, EvtPayload evt)
    {
        if (!_pairing.TryGetKeys(phoneId, out var keys)) return null;
        return _codec.SealEvent(phoneId, keys.SendKey, evt);
    }
}
