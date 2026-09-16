using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Ipc;
using PcMate.Agent.Service.Net;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Readiness;
using PcMate.Agent.Service.State;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

/// <summary>Снимок состояния ПК для телефона: питание, нагрузка, сеть, сеанс пользователя.</summary>
[SupportedOSPlatform("windows")]
public sealed class StatusProvider
{
    private readonly AgentStore _store;
    private readonly SessionHost _session;
    private readonly PowerController _power;
    private readonly ReadinessChecker _readiness;
    private readonly ILogger<StatusProvider> _log;

    private ulong _lastIdle, _lastKernel, _lastUser;
    private double _cpuPercent;
    private string? _activeWindow;
    private DateTimeOffset _activeWindowAt = DateTimeOffset.MinValue;

    public StatusProvider(AgentStore store, SessionHost session, PowerController power,
        ReadinessChecker readiness, ILogger<StatusProvider> log)
    {
        _store = store;
        _session = session;
        _power = power;
        _readiness = readiness;
        _log = log;
        SampleCpu();
    }

    public string AgentVersion { get; } =
        typeof(StatusProvider).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<StatusSnapshot> GetAsync(bool includeActiveWindow = true, CancellationToken ct = default)
    {
        var (ramUsed, ramTotal) = ReadMemory();
        var battery = ReadBattery();
        var user = _session.ActiveUser;

        if (includeActiveWindow) await RefreshActiveWindowAsync(ct).ConfigureAwait(false);

        return new StatusSnapshot
        {
            PcId = _store.Identity.DeviceId,
            PcName = _store.Identity.PcName,
            State = PowerStates.Running,
            UptimeSec = (long)(NativeMethods.GetTickCount64() / 1000),
            CpuPercent = Math.Round(SampleCpu(), 1),
            RamUsedMb = ramUsed,
            RamTotalMb = ramTotal,
            Battery = battery,
            ActiveWindow = _activeWindow,
            UserSession = new UserSessionInfo
            {
                Active = _session.HasUserSession,
                UserName = user?.UserName,
                Locked = false
            },
            Network = NetworkProbe.Describe(),
            AgentVersion = AgentVersion,
            Readiness = _readiness.LastResults.Count > 0 ? _readiness.Summarize() : null,
            PendingPower = _power.Pending,
            At = TimeUtil.IsoNow()
        };
    }

    public AgentInfo GetInfo() => new()
    {
        Version = AgentVersion,
        BuildDate = File.GetLastWriteTimeUtc(typeof(StatusProvider).Assembly.Location).ToString("yyyy-MM-dd"),
        Os = Environment.OSVersion.VersionString,
        PcName = _store.Identity.PcName,
        Features = new List<string>
        {
            "power", "scenarios", "schedules", "wake-timers", "wizard", "apps-list", "lan", "wol"
        }
    };

    private async Task RefreshActiveWindowAsync(CancellationToken ct)
    {
        // Активное окно спрашиваем у помощника не чаще раза в 5 секунд.
        if (DateTimeOffset.UtcNow - _activeWindowAt < TimeSpan.FromSeconds(5)) return;
        _activeWindowAt = DateTimeOffset.UtcNow;

        if (!_session.HasUserSession)
        {
            _activeWindow = null;
            return;
        }

        try
        {
            _activeWindow = await _session
                .RequestAsync<string>(IpcKinds.ActiveWindow, timeout: TimeSpan.FromSeconds(2), ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogTrace(ex, "Помощник не ответил про активное окно");
            _activeWindow = null;
        }
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return _cpuPercent;

        var idleT = idle.ToUInt64();
        var kernelT = kernel.ToUInt64();
        var userT = user.ToUInt64();

        if (_lastKernel != 0)
        {
            var idleDelta = idleT - _lastIdle;
            var totalDelta = kernelT - _lastKernel + (userT - _lastUser);
            if (totalDelta > 0)
                _cpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _lastIdle = idleT;
        _lastKernel = kernelT;
        _lastUser = userT;
        return _cpuPercent;
    }

    private static (long UsedMb, long TotalMb) ReadMemory()
    {
        var status = new NativeMethods.MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status)) return (0, 0);

        var total = (long)(status.ullTotalPhys / (1024 * 1024));
        var free = (long)(status.ullAvailPhys / (1024 * 1024));
        return (total - free, total);
    }

    private static BatteryInfo? ReadBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status)) return null;
        if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255) return null; // батареи нет

        return new BatteryInfo
        {
            Percent = status.BatteryLifePercent,
            Charging = status.ACLineStatus == 1
        };
    }
}
