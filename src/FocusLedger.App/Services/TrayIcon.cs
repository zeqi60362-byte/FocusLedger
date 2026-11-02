using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FocusLedger.Core;

namespace FocusLedger.App.Services;

/// <summary>
/// 托盘图标：颜色跟随当前状态。
/// 深度专注=绿、静默专注=蓝、分心=红、离席=灰、已暂停=深灰空心。
/// 图标按状态缓存——每次新建 Icon 再销毁 handle，会让代理/资源管理器拿到失效图标。
/// </summary>
internal static class TrayIcon
{
    private static readonly Dictionary<string, Icon> Cache = new();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Build(bool recording, ActivityState? state)
    {
        string cacheKey = recording ? $"rec:{state?.ToString() ?? "none"}" : "paused";
        if (Cache.TryGetValue(cacheKey, out Icon? cached))
        {
            return cached;
        }

        Color ring = !recording
            ? Color.FromArgb(255, 110, 116, 126)
            : state switch
            {
                ActivityState.ActiveFocus => Color.FromArgb(255, 74, 222, 128),
                ActivityState.SilentFocus => Color.FromArgb(255, 96, 165, 250),
                ActivityState.OffTask => Color.FromArgb(255, 248, 113, 113),
                ActivityState.Away => Color.FromArgb(255, 156, 163, 175),
                // 刚启动、还没攒够一个窗口时用中性灰，不要假装"很专注"
                _ => Color.FromArgb(255, 140, 146, 156)
            };

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // 深色底片，保证在浅色任务栏上也有对比
            using (var back = new SolidBrush(Color.FromArgb(recording ? 255 : 200, 22, 24, 28)))
            {
                graphics.FillEllipse(back, 0, 0, 31, 31);
            }

            using (var pen = new Pen(ring, 3.6f))
            {
                graphics.DrawArc(pen, 5, 5, 21, 21, -90, 359.9f);
            }

            if (recording)
            {
                using var dot = new SolidBrush(ring);
                graphics.FillEllipse(dot, 12, 12, 8, 8);
            }
        }

        // 注意：不要把 handle 销毁掉，Icon 缓存要长期存活
        IntPtr handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        Cache[cacheKey] = icon;
        return icon;
    }
}
