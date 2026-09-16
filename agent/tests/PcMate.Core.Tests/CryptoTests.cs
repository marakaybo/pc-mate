using System.Security.Cryptography;
using System.Text;
using PcMate.Core.Crypto;
using PcMate.Core.Protocol;
using PcMate.Core.Util;
using Xunit;

namespace PcMate.Core.Tests;

public class CryptoTests
{
    [Fact]
    public void X25519_BothSidesDeriveSameSecret()
    {
        var pc = KeyPairX25519.Generate();
        var phone = KeyPairX25519.Generate();

        var a = pc.Agree(phone.PublicKey);
        var b = phone.Agree(pc.PublicKey);

        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void SessionKeys_AreDirectional()
    {
        var pc = KeyPairX25519.Generate();
        var phone = KeyPairX25519.Generate();
        var pairId = Guid.NewGuid().ToString("D");

        var pcKeys = SessionKeys.Derive(pc.Agree(phone.PublicKey), pairId, PairRole.Pc);
        var phoneKeys = SessionKeys.Derive(phone.Agree(pc.PublicKey), pairId, PairRole.Phone);

        // То, чем шифрует телефон, ПК должен уметь расшифровать — и наоборот.
        Assert.Equal(phoneKeys.SendKey, pcKeys.ReceiveKey);
        Assert.Equal(pcKeys.SendKey, phoneKeys.ReceiveKey);

        // Направления не совпадают: отражение сообщения не пройдёт.
        Assert.NotEqual(pcKeys.SendKey, pcKeys.ReceiveKey);
    }

    [Fact]
    public void SessionKeys_DifferentPairIdGivesDifferentKeys()
    {
        var pc = KeyPairX25519.Generate();
        var phone = KeyPairX25519.Generate();
        var shared = pc.Agree(phone.PublicKey);

        var first = SessionKeys.Derive(shared, "pair-1", PairRole.Pc);
        var second = SessionKeys.Derive(shared, "pair-2", PairRole.Pc);

        Assert.NotEqual(first.SendKey, second.SendKey);
    }

    [Fact]
    public void SecureChannel_RoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Привет, PC MATE! 🖥");
        var aad = SecureChannel.BuildAad(1, "id-1", "from", "to", 1757894400);

        var (nonce, cipher) = SecureChannel.Seal(key, plaintext, aad);
        var opened = SecureChannel.Open(key, nonce, cipher, aad);

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void SecureChannel_RejectsTamperedRoutingFields()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("power.shutdown");
        var aad = SecureChannel.BuildAad(1, "id-1", "phone", "pc", 1757894400);

        var (nonce, cipher) = SecureChannel.Seal(key, plaintext, aad);
        var forgedAad = SecureChannel.BuildAad(1, "id-1", "phone", "other-pc", 1757894400);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            SecureChannel.Open(key, nonce, cipher, forgedAad));
    }

    [Fact]
    public void Fingerprint_IsOrderIndependentAndStable()
    {
        var a = RandomNumberGenerator.GetBytes(32);
        var b = RandomNumberGenerator.GetBytes(32);

        var first = Fingerprint.ForPair(a, b);
        var second = Fingerprint.ForPair(b, a);

        Assert.Equal(first, second);
        Assert.Equal(6 * 4 + 5, first.Length); // 6 групп по 4 символа и 5 дефисов
        Assert.DoesNotContain('I', first);     // Crockford Base32 исключает похожие символы
        Assert.DoesNotContain('L', first);
        Assert.DoesNotContain('U', first);
    }

    [Fact]
    public void B64Url_RoundTripsWithoutPadding()
    {
        for (var length = 1; length <= 40; length++)
        {
            var data = RandomNumberGenerator.GetBytes(length);
            var encoded = B64Url.Encode(data);

            Assert.DoesNotContain('=', encoded);
            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.Equal(data, B64Url.Decode(encoded));
        }
    }

    [Fact]
    public void MessageCodec_SealsAndOpensCommand()
    {
        var pc = KeyPairX25519.Generate();
        var phone = KeyPairX25519.Generate();
        const string pairId = "pair-abc";

        var phoneKeys = SessionKeys.Derive(phone.Agree(pc.PublicKey), pairId, PairRole.Phone);
        var pcKeys = SessionKeys.Derive(pc.Agree(phone.PublicKey), pairId, PairRole.Pc);

        var phoneCodec = new MessageCodec("phone-id");
        var pcCodec = new MessageCodec("pc-id");

        var envelope = phoneCodec.SealCommand("pc-id", phoneKeys.SendKey, new CmdPayload
        {
            Cmd = Commands.PowerShutdown,
            Args = System.Text.Json.Nodes.JsonNode.Parse("""{"delaySec":30}""")
        });

        var opened = pcCodec.Open<CmdPayload>(envelope, pcKeys.ReceiveKey);

        Assert.True(opened.Ok);
        Assert.Equal(Commands.PowerShutdown, opened.Value!.Cmd);
        Assert.Equal(30, opened.Value.Args!["delaySec"]!.GetValue<int>());
    }

    [Fact]
    public void MessageCodec_RejectsReplayedCommand()
    {
        var pc = KeyPairX25519.Generate();
        var phone = KeyPairX25519.Generate();
        const string pairId = "pair-replay";

        var phoneKeys = SessionKeys.Derive(phone.Agree(pc.PublicKey), pairId, PairRole.Phone);
        var pcKeys = SessionKeys.Derive(pc.Agree(phone.PublicKey), pairId, PairRole.Pc);

        var phoneCodec = new MessageCodec("phone-id");
        var pcCodec = new MessageCodec("pc-id");

        var envelope = phoneCodec.SealCommand("pc-id", phoneKeys.SendKey,
            new CmdPayload { Cmd = Commands.PowerSleep });

        Assert.True(pcCodec.Open<CmdPayload>(envelope, pcKeys.ReceiveKey).Ok);

        // Тот же перехваченный конверт второй раз приниматься не должен.
        var replay = pcCodec.Open<CmdPayload>(envelope, pcKeys.ReceiveKey);
        Assert.False(replay.Ok);
        Assert.Equal(ErrorCodes.Replay, replay.ErrorCode);
    }

    [Fact]
    public void MessageCodec_RejectsWrongKey()
    {
        var phone = KeyPairX25519.Generate();
        var pc = KeyPairX25519.Generate();
        var stranger = KeyPairX25519.Generate();

        var phoneKeys = SessionKeys.Derive(phone.Agree(pc.PublicKey), "p", PairRole.Phone);
        var strangerKeys = SessionKeys.Derive(stranger.Agree(pc.PublicKey), "p", PairRole.Pc);

        var envelope = new MessageCodec("phone").SealCommand("pc", phoneKeys.SendKey,
            new CmdPayload { Cmd = Commands.PowerLock });

        var opened = new MessageCodec("pc").Open<CmdPayload>(envelope, strangerKeys.ReceiveKey);

        Assert.False(opened.Ok);
        Assert.Equal(ErrorCodes.Crypto, opened.ErrorCode);
    }
}

public class ReplayGuardTests
{
    [Fact]
    public void AcceptsFirstRejectsDuplicate()
    {
        var guard = new ReplayGuard();
        var nonce = SecureChannel.NewNonceB64();
        var now = TimeUtil.UnixNow();

        Assert.Equal(ReplayGuard.Verdict.Accepted, guard.Check(nonce, now));
        Assert.Equal(ReplayGuard.Verdict.DuplicateNonce, guard.Check(nonce, now));
    }

    [Fact]
    public void RejectsStaleTimestamp()
    {
        var guard = new ReplayGuard(clockSkew: TimeSpan.FromSeconds(120));
        var old = TimeUtil.UnixNow() - 600;

        Assert.Equal(ReplayGuard.Verdict.StaleTimestamp, guard.Check(SecureChannel.NewNonceB64(), old));
    }

    [Fact]
    public void RejectsFutureTimestamp()
    {
        var guard = new ReplayGuard(clockSkew: TimeSpan.FromSeconds(120));
        var future = TimeUtil.UnixNow() + 600;

        Assert.Equal(ReplayGuard.Verdict.StaleTimestamp, guard.Check(SecureChannel.NewNonceB64(), future));
    }

    [Fact]
    public void RejectsMissingNonce()
    {
        var guard = new ReplayGuard();
        Assert.Equal(ReplayGuard.Verdict.MissingNonce, guard.Check("", TimeUtil.UnixNow()));
    }
}
