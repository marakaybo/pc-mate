using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcMate.Agent.Service.Readiness;

/// <summary>
/// Читает возможности питания напрямую у ядра (CallNtPowerInformation).
/// Это надёжнее разбора текста powercfg: не зависит от языка Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class PowerCapabilities
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerCapabilities
    {
        [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool SleepButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool LidPresent;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS1;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS2;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS5;
        [MarshalAs(UnmanagedType.U1)] public bool HiberFilePresent;
        [MarshalAs(UnmanagedType.U1)] public bool FullWake;
        [MarshalAs(UnmanagedType.U1)] public bool VideoDimPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ApmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool UpsPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ThermalControl;
        [MarshalAs(UnmanagedType.U1)] public bool ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorMaxThrottle;
        [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool Hiberboot;
        [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool AoAc;
        [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
        public byte HiberFileType;
        [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] spare3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent;
        [MarshalAs(UnmanagedType.U1)] public bool BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BatteryReportingScale[] BatteryScale;
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }

    private const int SystemPowerCapabilitiesLevel = 4;

    [LibraryImport("powrprof.dll", EntryPoint = "CallNtPowerInformation")]
    private static partial uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength);

    public sealed record Capabilities(
        bool S1, bool S2, bool S3, bool S4, bool S5,
        bool HiberFilePresent, bool Hiberboot, bool WakeAlarmPresent,
        bool ModernStandby, bool BatteriesPresent, int RtcWake)
    {
        /// <summary>Есть ли состояние, из которого ПК реально просыпается (S3 или S4).</summary>
        public bool HasUsableSleepState => S3 || S4;

        public string DescribeStates()
        {
            var states = new List<string>();
            if (S1) states.Add("S1");
            if (S2) states.Add("S2");
            if (S3) states.Add("S3 (сон)");
            if (S4) states.Add("S4 (гибернация)");
            if (S5) states.Add("S5 (выключение)");
            if (ModernStandby) states.Add("Modern Standby (S0 low-power)");
            return states.Count == 0 ? "не определены" : string.Join(", ", states);
        }
    }

    public static Capabilities? Query()
    {
        var size = Marshal.SizeOf<SystemPowerCapabilities>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = CallNtPowerInformation(SystemPowerCapabilitiesLevel, IntPtr.Zero, 0, buffer, (uint)size);
            if (status != 0) return null;

            var caps = Marshal.PtrToStructure<SystemPowerCapabilities>(buffer);
            return new Capabilities(
                caps.SystemS1, caps.SystemS2, caps.SystemS3, caps.SystemS4, caps.SystemS5,
                caps.HiberFilePresent, caps.Hiberboot, caps.WakeAlarmPresent,
                caps.AoAc, caps.SystemBatteriesPresent, caps.RtcWake);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
