using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcMate.Agent.Tray.Interop;

[SupportedOSPlatform("windows")]
internal static partial class NativeUi
{
    [LibraryImport("user32.dll", EntryPoint = "LockWorkStation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int count);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "ProcessIdToSessionId")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
    private static partial uint GetCurrentProcessId();

    /// <summary>Заголовок активного окна и имя его процесса — для карточки статуса на телефоне.</summary>
    public static string? GetActiveWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var buffer = new char[512];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        var title = length > 0 ? new string(buffer, 0, length) : "";

        string? processName = null;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0) processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            // Процесс мог завершиться между вызовами.
        }

        if (string.IsNullOrWhiteSpace(title)) return processName;
        return processName is null ? title : $"{processName} — {title}";
    }

    public static int CurrentSessionId()
    {
        try
        {
            return ProcessIdToSessionId(GetCurrentProcessId(), out var session) ? (int)session : 0;
        }
        catch
        {
            return 0;
        }
    }
}
