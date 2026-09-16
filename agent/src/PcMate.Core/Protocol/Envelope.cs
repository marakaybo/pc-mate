using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PcMate.Core.Crypto;
using PcMate.Core.Util;

namespace PcMate.Core.Protocol;

public static class MessageType
{
    public const string Cmd = "cmd";
    public const string Res = "res";
    public const string Evt = "evt";
    public const string Sys = "sys";
}

public sealed class EncBlock
{
    [JsonPropertyName("alg")] public string Alg { get; set; } = SecureChannel.Algorithm;
    [JsonPropertyName("n")] public string Nonce { get; set; } = "";
    [JsonPropertyName("c")] public string Cipher { get; set; } = "";
}

/// <summary>Конверт протокола v1 (см. docs/protocol.md §3).</summary>
public sealed class Envelope
{
    public const int ProtocolVersion = 1;
    public const string ServerAddress = "server";
    public const string BroadcastAddress = "*";

    [JsonPropertyName("v")] public int V { get; set; } = ProtocolVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("D");
    [JsonPropertyName("re")] public string? Re { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = MessageType.Cmd;
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; } = TimeUtil.UnixNow();
    [JsonPropertyName("enc")] public EncBlock? Enc { get; set; }

    // Только для type = "sys": незашифрованный служебный канал устройство ↔ сервер.
    [JsonPropertyName("sys")] public string? Sys { get; set; }
    [JsonPropertyName("data")] public JsonNode? Data { get; set; }

    public byte[] Aad() => SecureChannel.BuildAad(V, Id, From, To, Ts);

    public static Envelope Sys_(string from, string sys, object? data = null) => new()
    {
        Type = MessageType.Sys,
        From = from,
        To = ServerAddress,
        Sys = sys,
        Data = data is null ? null : JsonSerializer.SerializeToNode(data, PcMateJson.Options)
    };

    public T? DataAs<T>() => Data is null ? default : Data.Deserialize<T>(PcMateJson.Options);
}
