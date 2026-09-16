using System.Security.Cryptography;
using System.Text;

namespace PcMate.Core.Crypto;

/// <summary>
/// Визуальный отпечаток пары ключей. ПК и телефон обязаны показать одинаковую строку —
/// это защита от подмены ключа посредником при сопряжении.
/// </summary>
public static class Fingerprint
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford Base32

    public static string ForPair(byte[] pubA, byte[] pubB)
    {
        var (first, second) = Compare(pubA, pubB) <= 0 ? (pubA, pubB) : (pubB, pubA);

        using var sha = SHA256.Create();
        var prefix = Encoding.UTF8.GetBytes("pcmate-fp/v1");
        var buffer = new byte[prefix.Length + first.Length + second.Length];
        Buffer.BlockCopy(prefix, 0, buffer, 0, prefix.Length);
        Buffer.BlockCopy(first, 0, buffer, prefix.Length, first.Length);
        Buffer.BlockCopy(second, 0, buffer, prefix.Length + first.Length, second.Length);

        return Format(sha.ComputeHash(buffer), groups: 6);
    }

    /// <summary>Короткий отпечаток одного ключа — печатается в QR для предварительной сверки.</summary>
    public static string ForKey(byte[] pub, int groups = 3)
    {
        using var sha = SHA256.Create();
        var prefix = Encoding.UTF8.GetBytes("pcmate-key/v1");
        var buffer = new byte[prefix.Length + pub.Length];
        Buffer.BlockCopy(prefix, 0, buffer, 0, prefix.Length);
        Buffer.BlockCopy(pub, 0, buffer, prefix.Length, pub.Length);
        return Format(sha.ComputeHash(buffer), groups);
    }

    private static string Format(byte[] hash, int groups)
    {
        var chars = groups * 4;
        var sb = new StringBuilder(chars + groups);
        var bits = 0;
        var acc = 0;
        var produced = 0;

        foreach (var b in hash)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5 && produced < chars)
            {
                bits -= 5;
                if (produced > 0 && produced % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[(acc >> bits) & 0x1F]);
                produced++;
            }
            if (produced >= chars) break;
        }

        return sb.ToString();
    }

    private static int Compare(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}
