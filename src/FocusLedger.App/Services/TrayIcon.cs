using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using FocusLedger.Core;

namespace FocusLedger.App.Services;

/// <summary>
/// 托盘图标：深色圆角方块 + 粗体 F，F 的颜色表示当前状态。
/// 按状态缓存，并且一次生成 16/20/24/32 四个尺寸——让系统挑原生尺寸，
/// 避免拿大图缩小导致发糊。
/// </summary>
internal static class TrayIcon
{
    private static readonly int[] Sizes = { 16, 20, 24, 32 };
    private static readonly Dictionary<string, Icon> Cache = new();

    /// <summary>Icon 会在内部持有流，必须保持存活，不能释放。</summary>
    private static readonly List<MemoryStream> Streams = new();

    public static Icon Build(bool recording, ActivityState? state)
    {
        Color color = !recording
            ? Color.FromArgb(255, 150, 155, 165)
            : state switch
            {
                ActivityState.ActiveFocus => Color.FromArgb(255, 74, 222, 128),
                ActivityState.SilentFocus => Color.FromArgb(255, 96, 165, 250),
                ActivityState.OffTask => Color.FromArgb(255, 248, 113, 113),
                ActivityState.Away => Color.FromArgb(255, 160, 165, 175),
                _ => Color.FromArgb(255, 150, 155, 165)
            };

        string key = $"{recording}:{color.ToArgb()}";
        if (Cache.TryGetValue(key, out Icon? cached))
        {
            return cached;
        }

        var stream = new MemoryStream();
        WriteIco(stream, color);
        Streams.Add(stream);
        stream.Position = 0;

        var icon = new Icon(stream);
        Cache[key] = icon;
        return icon;
    }

    private static void WriteIco(MemoryStream output, Color color)
    {
        var images = new List<(int Size, byte[] Data)>();
        foreach (int size in Sizes)
        {
            using var bitmap = Render(size, color);
            images.Add((size, ToDib(bitmap)));
        }

        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)data.Length);
            writer.Write((uint)offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
        {
            writer.Write(data);
        }

        writer.Flush();
    }

    private static Bitmap Render(int size, Color color)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float pad = size * 0.03f + 0.4f;
        float radius = size * 0.24f;
        float side = size - pad * 2;

        using (var path = RoundedRect(pad, pad, side, side, radius))
        using (var background = new SolidBrush(Color.FromArgb(255, 20, 20, 24)))
        {
            g.FillPath(background, path);
        }

        DrawF(g, size, color);
        return bitmap;
    }

    /// <summary>粗体 F：先铺一层深色描边，再填状态色，保证在任何背景下都分得清。</summary>
    private static void DrawF(Graphics g, int size, Color color)
    {
        float s = size;
        float stemX = s * 0.30f;
        float stemW = Math.Max(2f, s * 0.15f);
        float top = s * 0.22f;
        float bottom = s * 0.78f;
        float topBarW = s * 0.44f;
        float midY = s * 0.445f;
        float midBarW = s * 0.33f;

        float outline = Math.Max(1f, s * 0.055f);
        using (var edge = new SolidBrush(Color.FromArgb(210, 8, 8, 10)))
        {
            g.FillRectangle(edge, stemX - outline, top - outline, stemW + outline * 2, (bottom - top) + outline * 2);
            g.FillRectangle(edge, stemX - outline, top - outline, topBarW + outline, stemW + outline * 2);
            g.FillRectangle(edge, stemX - outline, midY - outline, midBarW + outline, stemW + outline * 2);
        }

        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, stemX, top, stemW, bottom - top);
        g.FillRectangle(brush, stemX, top, topBarW, stemW);
        g.FillRectangle(brush, stemX, midY, midBarW, stemW);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>32 位 DIB：小尺寸用未压缩位图，系统不会拿大图缩放。</summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        int s = bitmap.Width;
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(40);
        writer.Write(s);
        writer.Write(s * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (int y = s - 1; y >= 0; y--)
        {
            for (int x = 0; x < s; x++)
            {
                Color c = bitmap.GetPixel(x, y);
                writer.Write(c.B);
                writer.Write(c.G);
                writer.Write(c.R);
                writer.Write(c.A);
            }
        }

        int maskRow = (s + 7) / 8;
        int padded = (maskRow + 3) / 4 * 4;
        writer.Write(new byte[padded * s]);
        writer.Flush();

        return ms.ToArray();
    }
}
