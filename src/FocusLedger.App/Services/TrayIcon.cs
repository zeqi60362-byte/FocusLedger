using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FocusLedger.App.Services;

/// <summary>托盘图标：记录中为绿色环，暂停为灰色环。运行时绘制，不依赖资源文件。</summary>
internal static class TrayIcon
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Build(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var background = recording
                ? Color.FromArgb(255, 24, 24, 27)
                : Color.FromArgb(255, 44, 44, 48);

            using (var brush = new SolidBrush(background))
            {
                graphics.FillEllipse(brush, 0, 0, 31, 31);
            }

            Color ring = recording
                ? Color.FromArgb(255, 74, 222, 128)
                : Color.FromArgb(255, 138, 138, 148);

            using (var pen = new Pen(ring, 3.2f))
            {
                graphics.DrawArc(pen, 6, 6, 19, 19, -80, 300);
            }

            using (var dot = new SolidBrush(ring))
            {
                graphics.FillEllipse(dot, 13, 13, 6, 6);
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
