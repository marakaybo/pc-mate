using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace PcMate.Core.Crypto;

/// <summary>Статическая пара ключей X25519 — идентичность устройства.</summary>
public sealed class KeyPairX25519
{
    public const int KeySize = 32;

    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }

    public string PublicKeyB64 => B64Url.Encode(PublicKey);

    private KeyPairX25519(byte[] priv, byte[] pub)
    {
        PrivateKey = priv;
        PublicKey = pub;
    }

    public static KeyPairX25519 Generate()
    {
        var seed = RandomNumberGenerator.GetBytes(KeySize);
        return FromPrivateKey(seed);
    }

    public static KeyPairX25519 FromPrivateKey(byte[] priv)
    {
        if (priv is null || priv.Length != KeySize)
            throw new ArgumentException($"Закрытый ключ X25519 должен быть {KeySize} байт.", nameof(priv));

        var p = new X25519PrivateKeyParameters(priv, 0);
        var pub = p.GeneratePublicKey().GetEncoded();
        return new KeyPairX25519((byte[])priv.Clone(), pub);
    }

    /// <summary>Вычисляет общий секрет X25519 с публичным ключом собеседника.</summary>
    public byte[] Agree(byte[] peerPublicKey)
    {
        if (peerPublicKey is null || peerPublicKey.Length != KeySize)
            throw new ArgumentException($"Публичный ключ X25519 должен быть {KeySize} байт.", nameof(peerPublicKey));

        var agreement = new X25519Agreement();
        agreement.Init(new X25519PrivateKeyParameters(PrivateKey, 0));
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey, 0), shared, 0);

        // Свёрнутый (all-zero) общий секрет означает ключ малого порядка — отвергаем.
        var acc = 0;
        foreach (var b in shared) acc |= b;
        if (acc == 0) throw new CryptographicException("Небезопасный общий секрет X25519 (ключ малого порядка).");

        return shared;
    }
}
