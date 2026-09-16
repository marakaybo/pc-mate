using System.Text;
using PcMate.Core.Crypto;
using PcMate.Core.Util;

namespace PcMate.Core.Protocol;

/// <summary>
/// Сборка и разбор зашифрованных конвертов. Одна точка, где встречаются
/// протокол, криптография и защита от повтора.
/// </summary>
public sealed class MessageCodec
{
    private readonly string _selfId;
    private readonly ReplayGuard _replay;

    public MessageCodec(string selfId, ReplayGuard? replayGuard = null)
    {
        _selfId = selfId;
        _replay = replayGuard ?? new ReplayGuard();
    }

    public Envelope Seal(string type, string to, byte[] sendKey, object payload, string? replyTo = null)
    {
        var env = new Envelope
        {
            Type = type,
            From = _selfId,
            To = to,
            Re = replyTo,
            Ts = TimeUtil.UnixNow()
        };

        var plaintext = Encoding.UTF8.GetBytes(PcMateJson.Serialize(payload));
        var (nonce, cipher) = SecureChannel.Seal(sendKey, plaintext, env.Aad());
        env.Enc = new EncBlock { Nonce = nonce, Cipher = cipher };
        return env;
    }

    public Envelope SealCommand(string to, byte[] sendKey, CmdPayload cmd) => Seal(MessageType.Cmd, to, sendKey, cmd);

    public Envelope SealResponse(string to, byte[] sendKey, ResPayload res, string replyTo)
        => Seal(MessageType.Res, to, sendKey, res, replyTo);

    public Envelope SealEvent(string to, byte[] sendKey, EvtPayload evt) => Seal(MessageType.Evt, to, sendKey, evt);

    public OpenResult<T> Open<T>(Envelope env, byte[] receiveKey) where T : class
    {
        if (env.V != Envelope.ProtocolVersion)
            return OpenResult<T>.Fail(ErrorCodes.Version, $"Неподдерживаемая версия протокола: {env.V}");

        if (env.Enc is null)
            return OpenResult<T>.Fail(ErrorCodes.Crypto, "Конверт без зашифрованной части.");

        if (!string.Equals(env.Enc.Alg, SecureChannel.Algorithm, StringComparison.OrdinalIgnoreCase))
            return OpenResult<T>.Fail(ErrorCodes.Crypto, $"Неподдерживаемый алгоритм: {env.Enc.Alg}");

        byte[] plain;
        try
        {
            plain = SecureChannel.Open(receiveKey, env.Enc.Nonce, env.Enc.Cipher, env.Aad());
        }
        catch (Exception ex)
        {
            return OpenResult<T>.Fail(ErrorCodes.Crypto, "Не удалось расшифровать сообщение: " + ex.Message);
        }

        var json = Encoding.UTF8.GetString(plain);
        if (!PcMateJson.TryDeserialize<T>(json, out var payload) || payload is null)
            return OpenResult<T>.Fail(ErrorCodes.BadArgs, "Не удалось разобрать полезную нагрузку.");

        var (nonce, ts) = ExtractReplayFields(payload);
        var verdict = _replay.Check(nonce, ts);
        if (verdict != ReplayGuard.Verdict.Accepted)
            return OpenResult<T>.Fail(ErrorCodes.Replay, verdict switch
            {
                ReplayGuard.Verdict.DuplicateNonce => "Повтор ранее полученной команды.",
                ReplayGuard.Verdict.StaleTimestamp => "Слишком большое расхождение времени между устройствами.",
                _ => "В сообщении отсутствует nonce."
            });

        return OpenResult<T>.Success(payload);
    }

    private static (string? nonce, long ts) ExtractReplayFields(object payload) => payload switch
    {
        CmdPayload c => (c.Nonce, c.Ts),
        ResPayload r => (r.Nonce, r.Ts),
        EvtPayload e => (e.Nonce, e.Ts),
        _ => (null, 0)
    };
}

public readonly struct OpenResult<T> where T : class
{
    public bool Ok { get; }
    public T? Value { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    private OpenResult(bool ok, T? value, string? code, string? message)
    {
        Ok = ok;
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
    }

    public static OpenResult<T> Success(T value) => new(true, value, null, null);
    public static OpenResult<T> Fail(string code, string message) => new(false, null, code, message);
}
