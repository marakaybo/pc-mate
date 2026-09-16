using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcMate.Agent.Tray.Interop;

/// <summary>
/// Громкость системы через Core Audio (IMMDeviceEnumerator → IAudioEndpointVolume).
/// Работает только в сеансе пользователя — поэтому шаг «громкость» исполняет помощник.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VolumeControl
{
    public static void SetLevel(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var endpoint = GetEndpointVolume();
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(percent / 100f, Guid.Empty));
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public static void SetMute(bool mute)
    {
        var endpoint = GetEndpointVolume();
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.SetMute(mute, Guid.Empty));
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public static int GetLevel()
    {
        var endpoint = GetEndpointVolume();
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level));
            return (int)Math.Round(level * 100);
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    private static IAudioEndpointVolume GetEndpointVolume()
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                             ?? throw new InvalidOperationException("Core Audio недоступен.");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device));
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var instance));
                return (IAudioEndpointVolume)instance;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr client);
        int UnregisterControlChangeNotify(IntPtr client);
        int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float levelDb, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
