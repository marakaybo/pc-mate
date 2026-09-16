using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PcMate.Core.Crypto;
using PcMate.Core.Util;

namespace PcMate.Core.Protocol;

/// <summary>Расшифрованная полезная нагрузка команды.</summary>
public sealed class CmdPayload
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("args")] public JsonNode? Args { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = SecureChannel.NewNonceB64();
    [JsonPropertyName("ts")] public long Ts { get; set; } = TimeUtil.UnixNow();

    public T? ArgsAs<T>() => Args is null ? default : Args.Deserialize<T>(PcMateJson.Options);
}

public sealed class ErrorInfo
{
    [JsonPropertyName("code")] public string Code { get; set; } = ErrorCodes.Unknown;
    [JsonPropertyName("message")] public string Message { get; set; } = "";

    public ErrorInfo() { }

    public ErrorInfo(string code, string message)
    {
        Code = code;
        Message = message;
    }
}

/// <summary>Расшифрованная полезная нагрузка ответа.</summary>
public sealed class ResPayload
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public JsonNode? Result { get; set; }
    [JsonPropertyName("error")] public ErrorInfo? Error { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = SecureChannel.NewNonceB64();
    [JsonPropertyName("ts")] public long Ts { get; set; } = TimeUtil.UnixNow();

    public static ResPayload Success(object? result = null) => new()
    {
        Ok = true,
        Result = result is null ? null : JsonSerializer.SerializeToNode(result, PcMateJson.Options)
    };

    public static ResPayload Failure(string code, string message) => new()
    {
        Ok = false,
        Error = new ErrorInfo(code, message)
    };

    public T? ResultAs<T>() => Result is null ? default : Result.Deserialize<T>(PcMateJson.Options);
}

/// <summary>Расшифрованная полезная нагрузка события.</summary>
public sealed class EvtPayload
{
    [JsonPropertyName("evt")] public string Evt { get; set; } = "";
    [JsonPropertyName("data")] public JsonNode? Data { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = SecureChannel.NewNonceB64();
    [JsonPropertyName("ts")] public long Ts { get; set; } = TimeUtil.UnixNow();

    public static EvtPayload Of(string evt, object? data = null) => new()
    {
        Evt = evt,
        Data = data is null ? null : JsonSerializer.SerializeToNode(data, PcMateJson.Options)
    };

    public T? DataAs<T>() => Data is null ? default : Data.Deserialize<T>(PcMateJson.Options);
}
