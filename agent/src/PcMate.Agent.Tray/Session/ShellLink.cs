using System.Text;

namespace PcMate.Agent.Tray.Session;

/// <summary>
/// Чтение ярлыков .lnk без COM: разбираем двоичный формат MS-SHLLINK.
/// Нужно, чтобы быстро собрать список программ из меню «Пуск» — COM-обращение
/// к каждому ярлыку занимало бы секунды.
/// </summary>
public sealed record ShellLinkInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? Description)
{
    public bool HasTarget => !string.IsNullOrWhiteSpace(TargetPath);
}

public static class ShellLink
{
    private const int HeaderSize = 0x4C;

    [Flags]
    private enum LinkFlags : uint
    {
        HasLinkTargetIdList = 1 << 0,
        HasLinkInfo = 1 << 1,
        HasName = 1 << 2,
        HasRelativePath = 1 << 3,
        HasWorkingDir = 1 << 4,
        HasArguments = 1 << 5,
        HasIconLocation = 1 << 6,
        IsUnicode = 1 << 7
    }

    public static ShellLinkInfo? Read(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Parse(bytes, path);
        }
        catch
        {
            return null;
        }
    }

    private static ShellLinkInfo? Parse(byte[] data, string linkPath)
    {
        if (data.Length < HeaderSize) return null;
        if (BitConverter.ToInt32(data, 0) != HeaderSize) return null;

        var flags = (LinkFlags)BitConverter.ToUInt32(data, 20);
        var offset = HeaderSize;

        if (flags.HasFlag(LinkFlags.HasLinkTargetIdList))
        {
            if (offset + 2 > data.Length) return null;
            var idListSize = BitConverter.ToUInt16(data, offset);
            offset += 2 + idListSize;
        }

        string? localBasePath = null;
        if (flags.HasFlag(LinkFlags.HasLinkInfo))
        {
            if (offset + 8 > data.Length) return null;
            var linkInfoStart = offset;
            var linkInfoSize = BitConverter.ToInt32(data, offset);
            var linkInfoHeaderSize = BitConverter.ToInt32(data, offset + 4);

            if (linkInfoSize > 0 && linkInfoStart + linkInfoSize <= data.Length && linkInfoHeaderSize >= 0x1C)
            {
                var linkInfoFlags = BitConverter.ToUInt32(data, offset + 8);
                var hasVolumeIdAndLocalBasePath = (linkInfoFlags & 1) != 0;

                if (hasVolumeIdAndLocalBasePath)
                {
                    if (linkInfoHeaderSize >= 0x24)
                    {
                        var unicodeOffset = BitConverter.ToInt32(data, offset + 28);
                        if (unicodeOffset > 0)
                            localBasePath = ReadNullTerminated(data, linkInfoStart + unicodeOffset, unicode: true);
                    }

                    if (string.IsNullOrEmpty(localBasePath))
                    {
                        var ansiOffset = BitConverter.ToInt32(data, offset + 16);
                        if (ansiOffset > 0)
                            localBasePath = ReadNullTerminated(data, linkInfoStart + ansiOffset, unicode: false);
                    }
                }

                offset = linkInfoStart + linkInfoSize;
            }
            else
            {
                return null;
            }
        }

        var unicodeStrings = flags.HasFlag(LinkFlags.IsUnicode);
        string? name = null, relativePath = null, workingDir = null, arguments = null;

        if (flags.HasFlag(LinkFlags.HasName)) name = ReadStringData(data, ref offset, unicodeStrings);
        if (flags.HasFlag(LinkFlags.HasRelativePath)) relativePath = ReadStringData(data, ref offset, unicodeStrings);
        if (flags.HasFlag(LinkFlags.HasWorkingDir)) workingDir = ReadStringData(data, ref offset, unicodeStrings);
        if (flags.HasFlag(LinkFlags.HasArguments)) arguments = ReadStringData(data, ref offset, unicodeStrings);

        var target = localBasePath;
        if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(relativePath))
        {
            try
            {
                var baseDir = Path.GetDirectoryName(linkPath);
                if (baseDir is not null)
                    target = Path.GetFullPath(Path.Combine(baseDir, relativePath!));
            }
            catch
            {
                // Относительный путь может быть некорректным — тогда ярлык пропускаем.
            }
        }

        if (string.IsNullOrWhiteSpace(target)) return null;

        if (string.IsNullOrWhiteSpace(workingDir))
        {
            try { workingDir = Path.GetDirectoryName(target); }
            catch { workingDir = null; }
        }

        return new ShellLinkInfo(target, NullIfEmpty(arguments), NullIfEmpty(workingDir), NullIfEmpty(name));
    }

    private static string? ReadStringData(byte[] data, ref int offset, bool unicode)
    {
        if (offset + 2 > data.Length) return null;
        var count = BitConverter.ToUInt16(data, offset);
        offset += 2;

        var byteCount = unicode ? count * 2 : count;
        if (offset + byteCount > data.Length) return null;

        var value = unicode
            ? Encoding.Unicode.GetString(data, offset, byteCount)
            : Encoding.Default.GetString(data, offset, byteCount);

        offset += byteCount;
        return value;
    }

    private static string? ReadNullTerminated(byte[] data, int start, bool unicode)
    {
        if (start < 0 || start >= data.Length) return null;

        if (unicode)
        {
            var end = start;
            while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0)) end += 2;
            return Encoding.Unicode.GetString(data, start, end - start);
        }

        var stop = start;
        while (stop < data.Length && data[stop] != 0) stop++;
        return Encoding.Default.GetString(data, start, stop - start);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
