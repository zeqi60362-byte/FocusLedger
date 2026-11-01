using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusLedger.Collector.Windows;

internal static class Native
{
    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    public const uint DesktopReadObjects = 0x0001;

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public static int GetIdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return -1;
        }

        uint tick = (uint)Environment.TickCount;
        return (int)((tick - info.dwTime) / 1000u);
    }

    public static bool IsDesktopLocked()
    {
        IntPtr handle = OpenInputDesktop(0, false, DesktopReadObjects);
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        CloseDesktop(handle);
        return false;
    }
}

/// <summary>当前前景窗口的快照。</summary>
public sealed record ForegroundInfo(
    IntPtr Hwnd,
    uint ProcessId,
    string App,
    string Title,
    int IdleSeconds,
    bool Locked,
    bool Minimized);

public static class DesktopProbe
{
    private const int MaxTitleLength = 300;

    public static ForegroundInfo Capture()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        _ = Native.GetWindowThreadProcessId(hwnd, out uint pid);

        string app = "unknown";
        if (pid > 0)
        {
            try
            {
                app = Process.GetProcessById((int)pid).ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                app = $"pid:{pid}";
            }
        }

        string title = ReadTitle(hwnd);
        int idle = Native.GetIdleSeconds();
        bool locked = Native.IsDesktopLocked();
        bool minimized = hwnd != IntPtr.Zero && Native.IsIconic(hwnd);

        return new ForegroundInfo(hwnd, pid, app, title, idle, locked, minimized);
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(MaxTitleLength);
        _ = Native.GetWindowText(hwnd, buffer, buffer.Capacity);
        string title = buffer.ToString();
        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }
}
