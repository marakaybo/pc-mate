namespace PcMate.Core.Crypto;

/// <summary>base64url без паддинга — единый формат бинарных значений в протоколе.</summary>
public static class B64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var s = Convert.ToBase64String(data);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Некорректная длина base64url-строки.");
        }
        return Convert.FromBase64String(s);
    }

    public static bool TryDecode(string? value, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value)) return false;
        try { data = Decode(value); return true; }
        catch { return false; }
    }
}
