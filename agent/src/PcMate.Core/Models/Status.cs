using System.Text.Json.Serialization;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Core.Models;

public sealed class BatteryInfo
{
    [JsonPropertyName("percent")] public int Percent { get; set; }
    [JsonPropertyName("charging")] public bool Charging { get; set; }
}

public sealed class UserSessionInfo
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
}

public sealed class NetworkInfo
{
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("adapter")] public string? Adapter { get; set; }
    [JsonPropertyName("isWired")] public bool IsWired { get; set; }
    [JsonPropertyName("broadcast")] public string? Broadcast { get; set; }
}

public sealed class ReadinessSummary
{
    [JsonPropertyName("ok")] public int Ok { get; set; }
    [JsonPropertyName("warn")] public int Warn { get; set; }
    [JsonPropertyName("fail")] public int Fail { get; set; }
    [JsonPropertyName("wakeReady")] public bool WakeReady { get; set; }
}

public sealed class StatusSnapshot
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = "";
    [JsonPropertyName("pcName")] public string PcName { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = PowerStates.Running;
    [JsonPropertyName("uptimeSec")] public long UptimeSec { get; set; }
    [JsonPropertyName("cpuPercent")] public double CpuPercent { get; set; }
    [JsonPropertyName("ramUsedMb")] public long RamUsedMb { get; set; }
    [JsonPropertyName("ramTotalMb")] public long RamTotalMb { get; set; }
    [JsonPropertyName("battery")] public BatteryInfo? Battery { get; set; }
    [JsonPropertyName("activeWindow")] public string? ActiveWindow { get; set; }
    [JsonPropertyName("userSession")] public UserSessionInfo? UserSession { get; set; }
    [JsonPropertyName("network")] public NetworkInfo? Network { get; set; }
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "1.0.0";
    [JsonPropertyName("readiness")] public ReadinessSummary? Readiness { get; set; }
    [JsonPropertyName("pendingPower")] public PendingPowerInfo? PendingPower { get; set; }
    [JsonPropertyName("at")] public string At { get; set; } = TimeUtil.IsoNow();
}

public sealed class PendingPowerInfo
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("secondsLeft")] public int SecondsLeft { get; set; }
    [JsonPropertyName("cancellable")] public bool Cancellable { get; set; } = true;
}

public sealed class CheckResult
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = CheckStatus.Unknown;
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("canFix")] public bool CanFix { get; set; }
    [JsonPropertyName("fixHint")] public string? FixHint { get; set; }
    [JsonPropertyName("docUrl")] public string? DocUrl { get; set; }
    [JsonPropertyName("requiresElevation")] public bool RequiresElevation { get; set; }
    [JsonPropertyName("manualSteps")] public List<string>? ManualSteps { get; set; }
    [JsonPropertyName("checkedAt")] public string CheckedAt { get; set; } = TimeUtil.IsoNow();
}

public static class CheckStatus
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Unknown = "unknown";
}

public static class CheckIds
{
    public const string HibernateEnabled = "hibernate-enabled";
    public const string SleepStates = "sleep-states";
    public const string WakeTimers = "wake-timers";
    public const string FastStartup = "fast-startup";
    public const string NicWakeArmed = "nic-wake-armed";
    public const string NicMagicPacket = "nic-magic-packet";
    public const string WiredConnection = "wired-connection";
    public const string NetworkIdentity = "network-identity";
    public const string StaticIp = "static-ip";
    public const string BiosWol = "bios-wol";
    public const string ServerLink = "server-link";
    public const string AutoLogon = "autologon";

    public static readonly string[] Ordered =
    {
        HibernateEnabled, SleepStates, WakeTimers, FastStartup,
        NicWakeArmed, NicMagicPacket, WiredConnection, NetworkIdentity,
        StaticIp, BiosWol, ServerLink, AutoLogon
    };
}

public sealed class AppEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("iconB64")] public string? IconB64 { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "start-menu";
    [JsonPropertyName("uwpAppId")] public string? UwpAppId { get; set; }
}

public sealed class EventRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("pcId")] public string PcId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = TimeUtil.IsoNow();
    [JsonPropertyName("source")] public string Source { get; set; } = "pc";
}

public sealed class AgentSettings
{
    [JsonPropertyName("countdownSec")] public int CountdownSec { get; set; } = 30;
    [JsonPropertyName("allowShellSteps")] public bool AllowShellSteps { get; set; }
    [JsonPropertyName("allowAutoLogon")] public bool AllowAutoLogon { get; set; }
    [JsonPropertyName("relayUrl")] public string RelayUrl { get; set; } = "";
    [JsonPropertyName("lanPort")] public int LanPort { get; set; } = 8760;
    [JsonPropertyName("lanEnabled")] public bool LanEnabled { get; set; } = true;
    [JsonPropertyName("heartbeatSec")] public int HeartbeatSec { get; set; } = 30;
    [JsonPropertyName("language")] public string Language { get; set; } = "ru";
    [JsonPropertyName("wakeTimersOnBattery")] public bool WakeTimersOnBattery { get; set; }
    [JsonPropertyName("logLevel")] public string LogLevel { get; set; } = "info";

    public AgentSettings Clone() => (AgentSettings)MemberwiseClone();
}

public sealed class AgentInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("buildDate")] public string BuildDate { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("pcName")] public string PcName { get; set; } = "";
    [JsonPropertyName("features")] public List<string> Features { get; set; } = new();
}

public sealed class PeerState
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = DeviceRoles.Pc;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = PowerStates.Unknown;
    [JsonPropertyName("lastSeen")] public string? LastSeen { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("lanIps")] public List<string>? LanIps { get; set; }
}
