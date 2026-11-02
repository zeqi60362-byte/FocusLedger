using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FocusLedger.Core;

namespace FocusLedger.App.Services;

/// <summary>
/// 托盘图标：颜色跟随当前状态。
/// 深度专注=绿、静默专注=蓝、分心=红、离席=灰、已暂停=深灰空心。
/// </summary>
internal static class TrayIcon
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Build(bool recording, ActivityState? state)
    {
        Color ring = !recording
            ? Color.FromArgb(255, 110, 116, 126)
            : state switch
            {
                ActivityState.ActiveFocus => Color.FromArgb(255, 74, 222, 128),
                ActivityState.SilentFocus => Color.FromArgb(255, 96, 165, 250),
                ActivityState.OffTask => Color.FromArgb(255, 248, 113, 113),
                ActivityState.Away => Color.FromArgb(255, 156, 163, 175),
                _ => Color.FromArgb(255, 74, 222, 128)
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

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
