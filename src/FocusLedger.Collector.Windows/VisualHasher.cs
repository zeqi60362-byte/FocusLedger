using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FocusLedger.Collector.Windows;

/// <summary>
/// 画面变化采样：把前景窗口缩到 9×8 灰度图，算 64 位 dHash。
/// 只保留哈希与汉明距离，绝不落盘任何图像（隐私红线）。
/// </summary>
public sealed class VisualHasher : IDisposable
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;

    private readonly Bitmap _small = new(HashWidth, HashHeight);
    private readonly double[] _luminance = new double[HashWidth * HashHeight];
    private readonly Graphics _smallGraphics;

    public VisualHasher()
    {
        _smallGraphics = Graphics.FromImage(_small);
        _smallGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
    }

    /// <summary>返回 64 位 dHash；窗口不可见或被最小化时返回 null。</summary>
    public ulong? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd))
        {
            return null;
        }

        if (!Native.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        // m0-findings §3.3：最小化窗口的 rect 是 -32000，必须裁剪到虚拟桌面
        int virtualLeft = Native.GetSystemMetrics(Native.SmXVirtualScreen);
        int virtualTop = Native.GetSystemMetrics(Native.SmYVirtualScreen);
        int virtualWidth = Native.GetSystemMetrics(Native.SmCxVirtualScreen);
        int virtualHeight = Native.GetSystemMetrics(Native.SmCyVirtualScreen);

        int left = Math.Max(rect.Left, virtualLeft);
        int top = Math.Max(rect.Top, virtualTop);
        int right = Math.Min(rect.Right, virtualLeft + virtualWidth);
        int bottom = Math.Min(rect.Bottom, virtualTop + virtualHeight);

        int width = right - left;
        int height = bottom - top;
        if (width <= 8 || height <= 8)
        {
            return null;
        }

        try
        {
            using var full = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(full))
            {
                graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height));
            }

            _smallGraphics.DrawImage(full, 0, 0, HashWidth, HashHeight);

            for (int y = 0; y < HashHeight; y++)
            {
                for (int x = 0; x < HashWidth; x++)
                {
                    Color c = _small.GetPixel(x, y);
                    _luminance[y * HashWidth + x] = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                }
            }

            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < HashHeight; y++)
            {
                for (int x = 0; x < HashWidth - 1; x++)
                {
                    if (_luminance[y * HashWidth + x] > _luminance[y * HashWidth + x + 1])
                    {
                        hash |= 1UL << bit;
                    }

                    bit++;
                }
            }

            return hash;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
        {
            // 截图失败（远程桌面、独占全屏、权限）时跳过本帧，不影响采集主流程
            return null;
        }
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public void Dispose()
    {
        _smallGraphics.Dispose();
        _small.Dispose();
    }
}
