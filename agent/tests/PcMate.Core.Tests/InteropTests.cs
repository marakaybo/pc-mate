using System.Text;
using System.Text.Json;
using PcMate.Core.Crypto;
using PcMate.Core.Protocol;
using PcMate.Core.Util;
using Xunit;

namespace PcMate.Core.Tests;

/// <summary>
/// Совместимость с другими реализациями протокола. Векторы посчитаны node:crypto
/// (tools/gen-test-vectors.mjs) — если агент сходится с ними, он сойдётся
/// и с телефоном, и с сервером.
/// </summary>
public class InteropTests
{
    private static readonly JsonElement Vectors = LoadVectors();

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-vectors.json");
        Assert.True(File.Exists(path), $"Не найден файл векторов: {path}. Запустите node tools/gen-test-vectors.mjs");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static byte[] B64(JsonElement parent, string property) =>
        B64Url.Decode(parent.GetProperty(property).GetString()!);

    [Fact]
    public void X25519_MatchesReferenceVectors()
    {
        var x = Vectors.GetProperty("x25519");

        var pc = KeyPairX25519.FromPrivateKey(B64(x, "pcPrivate"));
        var phone = KeyPairX25519.FromPrivateKey(B64(x, "phonePrivate"));

        Assert.Equal(B64(x, "pcPublic"), pc.PublicKey);
        Assert.Equal(B64(x, "phonePublic"), phone.PublicKey);
        Assert.Equal(B64(x, "sharedSecret"), pc.Agree(phone.PublicKey));
        Assert.Equal(B64(x, "sharedSecret"), phone.Agree(pc.PublicKey));
    }

    [Fact]
    public void Hkdf_MatchesReferenceVectors()
    {
        var x = Vectors.GetProperty("x25519");
        var h = Vectors.GetProperty("hkdf");

        var pc = KeyPairX25519.FromPrivateKey(B64(x, "pcPrivate"));
        var shared = pc.Agree(B64(x, "phonePublic"));
        var pairId = h.GetProperty("pairId").GetString()!;

        var pcKeys = SessionKeys.Derive(shared, pairId, PairRole.Pc);
        var phoneKeys = SessionKeys.Derive(shared, pairId, PairRole.Phone);

        Assert.Equal(B64(h, "keyPhoneToPc"), pcKeys.ReceiveKey);
        Assert.Equal(B64(h, "keyPcToPhone"), pcKeys.SendKey);
        Assert.Equal(B64(h, "keyPhoneToPc"), phoneKeys.SendKey);
        Assert.Equal(B64(h, "keyPcToPhone"), phoneKeys.ReceiveKey);
    }

    [Fact]
    public void Fingerprints_MatchReferenceVectors()
    {
        var x = Vectors.GetProperty("x25519");
        var f = Vectors.GetProperty("fingerprints");

        var pcPub = B64(x, "pcPublic");
        var phonePub = B64(x, "phonePublic");

        Assert.Equal(f.GetProperty("pair").GetString(), Fingerprint.ForPair(pcPub, phonePub));
        Assert.Equal(f.GetProperty("pcKey").GetString(), Fingerprint.ForKey(pcPub));
    }

    [Fact]
    public void AesGcm_DecryptsReferenceCiphertext()
    {
        var h = Vectors.GetProperty("hkdf");
        var a = Vectors.GetProperty("aesGcm");
        var envelope = a.GetProperty("envelope");

        var key = B64(h, "keyPhoneToPc");
        var aad = SecureChannel.BuildAad(
            envelope.GetProperty("v").GetInt32(),
            envelope.GetProperty("id").GetString()!,
            envelope.GetProperty("from").GetString()!,
            envelope.GetProperty("to").GetString()!,
            envelope.GetProperty("ts").GetInt64());

        Assert.Equal(a.GetProperty("aad").GetString(), Encoding.UTF8.GetString(aad));

        var plain = SecureChannel.Open(
            key,
            a.GetProperty("nonce").GetString()!,
            a.GetProperty("cipher").GetString()!,
            aad);

        Assert.Equal(a.GetProperty("plaintextJson").GetString(), Encoding.UTF8.GetString(plain));

        var payload = PcMateJson.Deserialize<CmdPayload>(Encoding.UTF8.GetString(plain));
        Assert.NotNull(payload);
        Assert.Equal("power.shutdown", payload!.Cmd);
        Assert.Equal(30, payload.Args!["delaySec"]!.GetValue<int>());
    }

    [Fact]
    public void AesGcm_ProducesIdenticalCiphertextForSameNonce()
    {
        var h = Vectors.GetProperty("hkdf");
        var a = Vectors.GetProperty("aesGcm");
        var envelope = a.GetProperty("envelope");

        var key = B64(h, "keyPhoneToPc");
        var aad = SecureChannel.BuildAad(
            envelope.GetProperty("v").GetInt32(),
            envelope.GetProperty("id").GetString()!,
            envelope.GetProperty("from").GetString()!,
            envelope.GetProperty("to").GetString()!,
            envelope.GetProperty("ts").GetInt64());

        var plaintext = Encoding.UTF8.GetBytes(a.GetProperty("plaintextJson").GetString()!);
        var nonce = B64(a, "nonce");

        // Повторяем шифрование с тем же nonce — результат обязан совпасть побайтно.
        using var aes = new System.Security.Cryptography.AesGcm(key, 16);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);

        var combined = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, tag.Length);

        Assert.Equal(a.GetProperty("cipher").GetString(), B64Url.Encode(combined));
    }

    [Fact]
    public void MagicPacket_MatchesReferenceVector()
    {
        var m = Vectors.GetProperty("magicPacket");
        var packet = MagicPacket.Build(m.GetProperty("mac").GetString()!);

        Assert.Equal(m.GetProperty("lengthBytes").GetInt32(), packet.Length);

        var prefix = Convert.ToHexString(packet[..12]).ToLowerInvariant();
        Assert.Equal(m.GetProperty("firstBytesHex").GetString(), prefix);
    }
}
