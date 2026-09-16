using System.Security.Cryptography;
using System.Text;

namespace PcMate.Core.Crypto;

/// <summary>Роль устройства в паре — определяет, какой ключ на приём, какой на передачу.</summary>
public enum PairRole
{
    Pc,
    Phone
}

/// <summary>
/// Пара направленных AES-256-GCM ключей, выведенных из общего секрета X25519.
/// Направленность исключает отражение сообщения обратно отправителю.
/// </summary>
public sealed class SessionKeys
{
    public const string InfoPhoneToPc = "pcmate/v1/phone->pc";
    public const string InfoPcToPhone = "pcmate/v1/pc->phone";

    /// <summary>Ключ, которым это устройство шифрует исходящие сообщения.</summary>
    public byte[] SendKey { get; }

    /// <summary>Ключ, которым это устройство расшифровывает входящие сообщения.</summary>
    public byte[] ReceiveKey { get; }

    private SessionKeys(byte[] send, byte[] receive)
    {
        SendKey = send;
        ReceiveKey = receive;
    }

    public static SessionKeys Derive(byte[] sharedSecret, string pairId, PairRole role)
    {
        if (sharedSecret is null || sharedSecret.Length == 0)
            throw new ArgumentException("Пустой общий секрет.", nameof(sharedSecret));
        if (string.IsNullOrWhiteSpace(pairId))
            throw new ArgumentException("pairId обязателен — он используется как salt HKDF.", nameof(pairId));

        var salt = Encoding.UTF8.GetBytes(pairId);
        var phoneToPc = HkdfSha256(sharedSecret, salt, InfoPhoneToPc, 32);
        var pcToPhone = HkdfSha256(sharedSecret, salt, InfoPcToPhone, 32);

        return role == PairRole.Pc
            ? new SessionKeys(send: pcToPhone, receive: phoneToPc)
            : new SessionKeys(send: phoneToPc, receive: pcToPhone);
    }

    public static byte[] HkdfSha256(byte[] ikm, byte[] salt, string info, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Encoding.UTF8.GetBytes(info));
}
