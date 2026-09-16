using System.Text.Json.Serialization;
using PcMate.Core.Crypto;
using PcMate.Core.Util;

namespace PcMate.Core.Models;

/// <summary>Содержимое QR-кода сопряжения (docs/protocol.md §8.1).</summary>
public sealed class PairingOffer
{
    public const string UriScheme = "pcmate";
    public const string UriPrefix = "pcmate://pair?d=";

    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("pcId")] public string PcId { get; set; } = "";
    [JsonPropertyName("pcName")] public string PcName { get; set; } = "";
    [JsonPropertyName("pub")] public string Pub { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("exp")] public long Exp { get; set; }
    [JsonPropertyName("relay")] public string? Relay { get; set; }
    [JsonPropertyName("lan")] public List<string> Lan { get; set; } = new();
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("fp")] public string? Fp { get; set; }

    [JsonIgnore] public bool IsExpired => TimeUtil.UnixNow() > Exp;

    public string ToUri() => UriPrefix + B64Url.Encode(System.Text.Encoding.UTF8.GetBytes(PcMateJson.Serialize(this)));

    public static PairingOffer? FromUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var idx = uri.IndexOf("d=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var payload = uri[(idx + 2)..];
        if (!B64Url.TryDecode(payload, out var bytes)) return null;
        return PcMateJson.Deserialize<PairingOffer>(System.Text.Encoding.UTF8.GetString(bytes));
    }
}

/// <summary>Запрос телефона на завершение сопряжения (POST /pair/claim или напрямую на агента).</summary>
public sealed class PairClaim
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("phoneId")] public string PhoneId { get; set; } = "";
    [JsonPropertyName("phonePub")] public string PhonePub { get; set; } = "";
    [JsonPropertyName("phoneName")] public string PhoneName { get; set; } = "";
    [JsonPropertyName("pairId")] public string? PairId { get; set; }
}

public sealed class PairResult
{
    [JsonPropertyName("pairId")] public string PairId { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("pcId")] public string? PcId { get; set; }
    [JsonPropertyName("pcPub")] public string? PcPub { get; set; }
    [JsonPropertyName("pcName")] public string? PcName { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("lanIps")] public List<string>? LanIps { get; set; }
    [JsonPropertyName("relay")] public string? Relay { get; set; }
    [JsonPropertyName("fingerprint")] public string? Fingerprint { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Сопряжённый телефон в хранилище агента.</summary>
public sealed class PairedPhone
{
    [JsonPropertyName("pairId")] public string PairId { get; set; } = "";
    [JsonPropertyName("phoneId")] public string PhoneId { get; set; } = "";
    [JsonPropertyName("phoneName")] public string PhoneName { get; set; } = "";
    [JsonPropertyName("phonePub")] public string PhonePub { get; set; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("pairedAt")] public string PairedAt { get; set; } = TimeUtil.IsoNow();
    [JsonPropertyName("lastSeenAt")] public string? LastSeenAt { get; set; }
    [JsonPropertyName("revoked")] public bool Revoked { get; set; }
}
