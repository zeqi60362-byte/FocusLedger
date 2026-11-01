using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.Control;

namespace FocusLedger.Probe;

/// <summary>
/// M0.2 信号探针：补齐 PowerShell 版无法验证的两类信号。
///   1. 媒体播放状态（WinRT SMTC）—— 决定"看网课"能否被正确识别
///   2. 锁屏状态（OpenInputDesktop）—— 决定"锁屏过夜"能否被排除
///
/// 只读、不落盘、不联网。不采集任何按键内容或画面。
/// </summary>
internal static class Program
{
    // ---------- Win32 ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;

    // ---------- helpers ----------

    private static int GetIdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return -1;
        uint tick = (uint)Environment.TickCount;
        return (int)((tick - li.dwTime) / 1000u);
    }

    private static bool IsDesktopLocked()
    {
        // 锁屏时输入桌面不可打开（ERROR_ACCESS_DENIED）
        IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return true;
        CloseDesktop(h);
        return false;
    }

    private static (string App, string Title) GetForeground()
    {
        IntPtr h = GetForegroundWindow();
        _ = GetWindowThreadProcessId(h, out uint pid);

        string app = "unknown";
        if (pid > 0)
        {
            try { app = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
            catch { app = $"pid:{pid}"; }
        }

        var sb = new StringBuilder(512);
        _ = GetWindowText(h, sb, sb.Capacity);
        string title = sb.ToString();
        if (title.Length > 60) title = title[..60] + "…";
        return (app, title);
    }

    private static async Task<string> DescribeMediaAsync(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        var sessions = mgr.GetSessions();
        if (sessions.Count == 0) return "no-session";

        var parts = new List<string>();
        foreach (var s in sessions)
        {
            string status;
            try { status = s.GetPlaybackInfo().PlaybackStatus.ToString(); }
            catch { status = "?"; }

            string title = "";
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
            }
            catch { /* 某些应用不允许读取媒体属性 */ }

            if (title.Length > 40) title = title[..40] + "…";
            parts.Add($"{ShortApp(s.SourceAppUserModelId)}={status}{(title.Length > 0 ? $" \"{title}\"" : "")}");
        }
        return string.Join(" | ", parts);
    }

    private static string ShortApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "?";
        int bang = aumid.IndexOf('!');
        return bang > 0 ? aumid[..bang] : aumid;
    }

    // ---------- main ----------

    private static async Task<int> Main(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 20;
        int intervalMs = args.Length > 1 && int.TryParse(args[1], out var i) ? i : 2000;

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine("FocusLedger · M0.2 信号探针 (.NET)");
        Console.WriteLine($"运行 {seconds}s，间隔 {intervalMs}ms");
        Console.WriteLine($"Runtime: {Environment.Version}  OS: {Environment.OSVersion.Version}");
        Console.WriteLine();

        GlobalSystemMediaTransportControlsSessionManager? mgr = null;
        try
        {
            mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            Console.WriteLine("[SMTC] 会话管理器已获取 —— 媒体信号可用");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SMTC] 获取失败: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("[SMTC] 该信号将降级：只能靠音频峰值判断是否有媒体");
        }
        Console.WriteLine();

        Console.WriteLine("  t     app                    idle  locked  media");
        Console.WriteLine("  ---------------------------------------------------------------");

        var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        int mediaObserved = 0, lockedObserved = 0, cycles = 0;
        string? firstMediaError = null;

        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                var (app, _) = GetForeground();
                int idle = GetIdleSeconds();
                bool locked = IsDesktopLocked();

                string media = "-";
                if (mgr is not null)
                {
                    try { media = await DescribeMediaAsync(mgr); }
                    catch (Exception ex) { media = "error"; firstMediaError ??= ex.Message; }
                }

                if (media is not "-" and not "no-session" and not "error") mediaObserved++;
                if (media.Contains("Playing", StringComparison.OrdinalIgnoreCase)) mediaObserved += 0;
                if (locked) lockedObserved++;
                cycles++;

                Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:F1}s  {app,-22} {idle,4}s  {(locked ? "YES" : "no"),-6}  {media}");

                await Task.Delay(intervalMs);
            }
        }
        finally
        {
            sw.Stop();
            proc.Refresh();
            double cpuMs = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds;
            double pct = sw.Elapsed.TotalMilliseconds > 0
                ? cpuMs / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100
                : 0;

            Console.WriteLine();
            Console.WriteLine("---------- 汇总 ----------");
            Console.WriteLine($"采样轮次        : {cycles}");
            Console.WriteLine($"媒体会话可见    : {mediaObserved}");
            Console.WriteLine($"锁屏观测        : {lockedObserved}");
            Console.WriteLine($"平均 CPU 占用   : {pct:F2}%  ({Environment.ProcessorCount} 核)");
            Console.WriteLine($"工作集内存      : {proc.WorkingSet64 / 1024.0 / 1024.0:F1} MB");
            if (firstMediaError is not null)
                Console.WriteLine($"媒体读取错误    : {firstMediaError}");
            Console.WriteLine();
        }

        return 0;
    }
}
