using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcMate.Core.Models;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Net;

/// <summary>
/// Определение сетевого адаптера, который на самом деле принимает магический пакет.
///
/// Задача не такая простая, как кажется: на обычном компьютере есть виртуальные
/// адаптеры (VPN, Hyper-V, VirtualBox, WSL), и некоторые из них выглядят как
/// обычный Ethernet со шлюзом. Разбудить компьютер они не могут — магический
/// пакет принимает только физическая сетевая карта.
/// </summary>
public static class NetworkProbe
{
    /// <summary>Признаки виртуальных адаптеров в имени или описании.</summary>
    private static readonly string[] VirtualMarkers =
    {
        "virtual", "виртуал", "vpn", "vmware", "virtualbox", "vbox", "hyper-v", "hyperv",
        "tap-", "tap ", "tun", "loopback", "pseudo", "radmin", "hamachi", "zerotier",
        "tailscale", "wireguard", "wsl", "docker", "npcap", "bluetooth", "miniport",
        "teredo", "isatap", "openvpn", "nordlynx", "proton", "wintun", "anydesk",
        "parsec", "nvidia network", "cisco anyconnect", "citrix", "juniper", "pangp",
        "sonicwall", "checkpoint", "forticlient", "netextender", "softether", "zte",
        "mobile broadband", "ras async", "wan miniport", "microsoft kernel debug"
    };

    public sealed record AdapterInfo(
        NetworkInterface Interface,
        string Name,
        string Description,
        string Mac,
        IPAddress? Ip,
        IPAddress? Broadcast,
        bool IsWired,
        bool IsDhcp,
        bool IsVirtual,
        int Score);

    /// <summary>
    /// Активный физический адаптер. Кандидаты ранжируются: виртуальные в самый низ,
    /// затем предпочтение проводному подключению со шлюзом и «настоящим» MAC-адресом.
    /// </summary>
    public static AdapterInfo? GetPrimaryAdapter() => EnumerateAdapters().FirstOrDefault();

    /// <summary>Все подходящие адаптеры по убыванию пригодности — для диагностики.</summary>
    public static List<AdapterInfo> EnumerateAdapters()
    {
        var candidates = new List<AdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var unicast = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(a.Address));
            if (unicast is null) continue;

            var mac = FormatMac(nic.GetPhysicalAddress());
            if (string.IsNullOrEmpty(mac)) continue;

            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address is not null && !g.Address.Equals(IPAddress.Any) &&
                          g.Address.AddressFamily == AddressFamily.InterNetwork);

            var isWired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;

            var isVirtual = LooksVirtual(nic.Name, nic.Description, nic.GetPhysicalAddress());
            var isDhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? true;

            var score = ScoreAdapter(isVirtual, isWired, hasGateway, unicast.Address, nic);

            candidates.Add(new AdapterInfo(
                nic, nic.Name, nic.Description, mac, unicast.Address,
                ComputeBroadcast(unicast.Address, unicast.IPv4Mask),
                isWired && !isVirtual, isDhcp, isVirtual, score));
        }

        return candidates.OrderByDescending(c => c.Score).ToList();
    }

    private static int ScoreAdapter(bool isVirtual, bool isWired, bool hasGateway, IPAddress ip, NetworkInterface nic)
    {
        var score = 0;

        // Виртуальный адаптер не разбудит компьютер — он существует только внутри Windows.
        if (isVirtual) score -= 1000;

        if (isWired) score += 100;
        else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 40;

        if (hasGateway) score += 50;
        if (IsPrivateAddress(ip)) score += 30;

        // Физический MAC выдан производителем: бит «locally administered» не установлен.
        var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
        if (macBytes.Length == 6 && (macBytes[0] & 0x02) == 0) score += 40;

        // Скорость канала косвенно отличает настоящую карту от программной.
        try
        {
            if (nic.Speed >= 100_000_000) score += 10;
        }
        catch (PlatformNotSupportedException)
        {
            // Некоторые драйверы не сообщают скорость — не страшно.
        }

        return score;
    }

    /// <summary>Домашние сети живут в приватных диапазонах; VPN обычно нет.</summary>
    private static bool IsPrivateAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;

        return b[0] switch
        {
            10 => true,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            _ => false
        };
    }

    private static bool LooksVirtual(string name, string description, PhysicalAddress mac)
    {
        var haystack = (name + " " + description).ToLowerInvariant();
        if (VirtualMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal))) return true;

        // Локально-администрируемый MAC почти всегда означает программный адаптер.
        var bytes = mac.GetAddressBytes();
        return bytes.Length == 6 && (bytes[0] & 0x02) != 0;
    }

    public static NetworkInfo Describe()
    {
        var adapter = GetPrimaryAdapter();
        return new NetworkInfo
        {
            Mac = adapter?.Mac,
            Ip = adapter?.Ip?.ToString(),
            Adapter = adapter?.Name,
            IsWired = adapter?.IsWired ?? false,
            Broadcast = adapter?.Broadcast?.ToString()
        };
    }

    /// <summary>Локальные адреса для сопряжения: виртуальные интерфейсы идут последними.</summary>
    public static List<string> LocalIps()
        => EnumerateAdapters()
            .Where(a => a.Ip is not null)
            .Select(a => a.Ip!.ToString())
            .Distinct()
            .ToList();

    public static string FormatMac(PhysicalAddress? address)
    {
        var bytes = address?.GetAddressBytes();
        return bytes is null || bytes.Length != 6 ? "" : MagicPacket.FormatMac(bytes);
    }

    private static IPAddress? ComputeBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask is null) return null;
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return null;

        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)~m[i]);
        return new IPAddress(b);
    }
}
