using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PcMate.Core.Util;

/// <summary>Wake-on-LAN: формирование и отправка магического пакета (docs/protocol.md §9).</summary>
public static partial class MagicPacket
{
    public static readonly int[] Ports = { 9, 7 };
    public const int DefaultRepeat = 3;

    public static byte[] Build(string mac)
    {
        var bytes = ParseMac(mac);
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(bytes, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static byte[] ParseMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) throw new ArgumentException("MAC-адрес пуст.", nameof(mac));
        var cleaned = MacSeparators().Replace(mac, "");
        if (cleaned.Length != 12) throw new FormatException($"Некорректный MAC-адрес: {mac}");

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));

    /// <summary>Отправляет пакет на все широковещательные адреса локальных подсетей и на 255.255.255.255.</summary>
    public static async Task<int> SendAsync(string mac, string? broadcastOverride = null,
        int repeat = DefaultRepeat, int intervalMs = 1000, CancellationToken ct = default)
    {
        var packet = Build(mac);
        var targets = new List<IPAddress>();

        if (!string.IsNullOrWhiteSpace(broadcastOverride) && IPAddress.TryParse(broadcastOverride, out var forced))
            targets.Add(forced);
        else
            targets.AddRange(EnumerateBroadcastAddresses());

        if (!targets.Any(a => a.Equals(IPAddress.Broadcast)))
            targets.Add(IPAddress.Broadcast);

        var sent = 0;
        for (var attempt = 0; attempt < Math.Max(1, repeat); attempt++)
        {
            foreach (var target in targets)
            {
                foreach (var port in Ports)
                {
                    try
                    {
                        using var udp = new UdpClient { EnableBroadcast = true };
                        await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, port)).ConfigureAwait(false);
                        sent++;
                    }
                    catch (SocketException)
                    {
                        // Интерфейс мог исчезнуть — пробуем остальные адреса.
                    }
                }
            }

            if (attempt < repeat - 1)
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
        }

        return sent;
    }

    public static IEnumerable<IPAddress> EnumerateBroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask is null) continue;

                var addrBytes = ua.Address.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(addrBytes[i] | (byte)~maskBytes[i]);

                yield return new IPAddress(broadcast);
            }
        }
    }

    /// <summary>Находятся ли два адреса в одной подсети (по маске /24 — эвристика для телефона).</summary>
    public static bool SameSubnet24(string? a, string? b)
    {
        if (!IPAddress.TryParse(a, out var ipA) || !IPAddress.TryParse(b, out var ipB)) return false;
        var x = ipA.GetAddressBytes();
        var y = ipB.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4) return false;
        return x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }

    [GeneratedRegex("[^0-9A-Fa-f]")]
    private static partial Regex MacSeparators();
}
