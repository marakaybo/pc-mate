using System.Security.Cryptography;
using System.Text;

namespace PcMate.Core.Crypto;

/// <summary>Сквозное шифрование полезной нагрузки конверта: AES-256-GCM + AAD из маршрутных полей.</summary>
public static class SecureChannel
{
    public const string Algorithm = "aes-256-gcm";
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>AAD связывает шифротекст с маршрутными полями конверта.</summary>
    public static byte[] BuildAad(int v, string id, string from, string to, long ts)
        => Encoding.UTF8.GetBytes($"{v}|{id}|{from}|{to}|{ts}");

    public static (string NonceB64, string CipherB64) Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);

        var combined = new byte[cipher.Length + TagSize];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, TagSize);

        return (B64Url.Encode(nonce), B64Url.Encode(combined));
    }

    public static byte[] Open(byte[] key, string nonceB64, string cipherB64, byte[] aad)
    {
        ValidateKey(key);
        var nonce = B64Url.Decode(nonceB64);
        if (nonce.Length != NonceSize)
            throw new CryptographicException("Некорректная длина nonce.");

        var combined = B64Url.Decode(cipherB64);
        if (combined.Length < TagSize)
            throw new CryptographicException("Шифротекст короче тега аутентификации.");

        var cipher = new byte[combined.Length - TagSize];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(combined, 0, cipher, 0, cipher.Length);
        Buffer.BlockCopy(combined, cipher.Length, tag, 0, TagSize);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    public static string NewNonceB64() => B64Url.Encode(RandomNumberGenerator.GetBytes(16));

    private static void ValidateKey(byte[] key)
    {
        if (key is null || key.Length != 32)
            throw new CryptographicException("Ключ AES-256-GCM должен быть 32 байта.");
    }
}
