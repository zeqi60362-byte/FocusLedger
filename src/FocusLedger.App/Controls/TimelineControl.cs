using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FocusLedger.Core;

namespace FocusLedger.App.Controls;

/// <summary>
/// 今日时间轴。自绘色带：横轴为时间，颜色为状态，低置信段降低不透明度。
/// 悬停显示该段详情，点击触发选中事件。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<Segment>),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowStartProperty = DependencyProperty.Register(
        nameof(WindowStart),
        typeof(long),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowEndProperty = DependencyProperty.Register(
        nameof(WindowEnd),
        typeof(long),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    private Segment? _hover;

    public TimelineControl()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<Segment>? Segments
    {
        get => (IReadOnlyList<Segment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long WindowStart
    {
        get => (long)GetValue(WindowStartProperty);
        set => SetValue(WindowStartProperty, value);
    }

    public long WindowEnd
    {
        get => (long)GetValue(WindowEndProperty);
        set => SetValue(WindowEndProperty, value);
    }

    public event EventHandler<Segment>? SegmentSelected;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var background = Res("TimelineBg", Color.FromRgb(0x14, 0x14, 0x17));
        dc.DrawRoundedRectangle(background, null, new Rect(0, 0, width, height), 16, 16);

        long start = WindowStart;
        long end = WindowEnd;
        if (end <= start)
        {
            return;
        }

        const double leftPad = 10;
        const double rightPad = 10;
        const double topPad = 30;
        const double bottomPad = 24;

        double plotWidth = width - leftPad - rightPad;
        double plotHeight = height - topPad - bottomPad;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        DrawHourGrid(dc, start, end, leftPad, topPad, plotWidth, plotHeight);

        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            DrawCentered(dc, "今天还没有数据", width, height);
            return;
        }

        var clip = new Rect(leftPad, topPad, plotWidth, plotHeight);
        dc.PushClip(new RectangleGeometry(clip, 8, 8));

        foreach (var segment in segments)
        {
            if (segment.EndUtcMs <= start || segment.StartUtcMs >= end)
            {
                continue;
            }

            double x1 = leftPad + (Math.Max(segment.StartUtcMs, start) - start) / (double)(end - start) * plotWidth;
            double x2 = leftPad + (Math.Min(segment.EndUtcMs, end) - start) / (double)(end - start) * plotWidth;

            double barWidth = Math.Max(1.2, x2 - x1);
            var brush = BrushFor(segment.State);

            dc.PushOpacity(0.92);
            dc.DrawRoundedRectangle(brush, null, new Rect(x1, topPad, barWidth, plotHeight), 3, 3);
            dc.Pop();
        }

        if (_hover is not null)
        {
            double hx1 = leftPad + (Math.Max(_hover.StartUtcMs, start) - start) / (double)(end - start) * plotWidth;
            double hx2 = leftPad + (Math.Min(_hover.EndUtcMs, end) - start) / (double)(end - start) * plotWidth;
            // 双层描边：外层面色 + 内层文字色，任何主题、任何色块上都清晰可见
            var hoverRect = new Rect(hx1, topPad, Math.Max(1.5, hx2 - hx1), plotHeight);
            dc.DrawRoundedRectangle(null, new Pen(Res("Surface", Colors.Black), 3.2), hoverRect, 3, 3);
            dc.DrawRoundedRectangle(null, new Pen(Res("Text", Colors.White), 1.2), hoverRect, 3, 3);
        }

        dc.Pop();

        DrawNowMarker(dc, start, end, leftPad, topPad, plotWidth, plotHeight);
        DrawHoverInfo(dc, width, topPad);
    }

    private void DrawHourGrid(
        DrawingContext dc,
        long start,
        long end,
        double leftPad,
        double topPad,
        double plotWidth,
        double plotHeight)
    {
        var localStart = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime();
        var localEnd = DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime();

        double spanHours = (end - start) / 3_600_000.0;
        int stepHours = spanHours switch
        {
            <= 2 => 0,      // 用 15 分钟刻度
            <= 8 => 1,
            <= 16 => 2,
            _ => 3
        };

        var tickBrush = Res("TimelineGrid", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        var pen = new Pen(tickBrush, 1);
        var labelBrush = Res("TimelineLabel", Color.FromRgb(0x7A, 0x7A, 0x84));
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var cursor = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0);
        int stepMinutes = stepHours == 0 ? 15 : stepHours * 60;

        while (cursor <= localEnd.DateTime)
        {
            long tickUtc = new DateTimeOffset(cursor, TimeZoneInfo.Local.GetUtcOffset(cursor)).ToUnixTimeMilliseconds();
            if (tickUtc >= start && tickUtc <= end)
            {
                double x = leftPad + (tickUtc - start) / (double)(end - start) * plotWidth;
                dc.DrawLine(pen, new Point(x, topPad), new Point(x, topPad + plotHeight));

                string label = stepHours == 0
                    ? cursor.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : cursor.ToString("HH", CultureInfo.InvariantCulture);

                var text = new FormattedText(
                    label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 10.5, labelBrush, dpi);
                dc.DrawText(text, new Point(x - text.Width / 2, topPad + plotHeight + 6));
            }

            cursor = cursor.AddMinutes(stepMinutes);
        }
    }

    private void DrawNowMarker(
        DrawingContext dc,
        long start,
        long end,
        double leftPad,
        double topPad,
        double plotWidth,
        double plotHeight)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < start || now > end)
        {
            return;
        }

        double x = leftPad + (now - start) / (double)(end - start) * plotWidth;
        var pen = new Pen(Res("TimelineLabel", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dot };
        dc.DrawLine(pen, new Point(x, topPad - 6), new Point(x, topPad + plotHeight));
    }

    private void DrawHoverInfo(DrawingContext dc, double width, double topPad)
    {
        if (_hover is null)
        {
            return;
        }

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var local = DateTimeOffset.FromUnixTimeMilliseconds(_hover.StartUtcMs).ToLocalTime();
        string text = $"{local:HH:mm}  {StateName(_hover.State)}  {_hover.App}  {_hover.Title}";

        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 11.5, Res("OverlayText", Color.FromRgb(0xD8, 0xD8, 0xDE)), dpi);

        double boxWidth = Math.Min(formatted.Width + 16, width - 20);
        var boxRect = new Rect(12, 6, boxWidth, 22);
        dc.DrawRoundedRectangle(Res("OverlayBg", Color.FromArgb(0xEE, 0x22, 0x22, 0x28)), null, boxRect, 11, 11);

        dc.PushClip(new RectangleGeometry(boxRect, 11, 11));
        dc.DrawText(formatted, new Point(20, 8));
        dc.Pop();
    }

    private void DrawCentered(DrawingContext dc, string message, double width, double height)
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 13, Res("TimelineLabel", Color.FromRgb(0x7A, 0x7A, 0x84)), dpi);

        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    private Brush BrushFor(ActivityState state) => state switch
    {
        ActivityState.ActiveFocus => Res("StateActive", Color.FromRgb(0x4A, 0xDE, 0x80)),
        ActivityState.SilentFocus => Res("StateSilent", Color.FromRgb(0x60, 0xA5, 0xFA)),
        ActivityState.OffTask => Res("StateOffTask", Color.FromRgb(0xF8, 0x71, 0x71)),
        _ => Res("StateIdle", Color.FromRgb(0x5B, 0x5B, 0x66))
    };

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static string StateName(ActivityState state) => StateNames.Chinese(state);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var point = e.GetPosition(this);
        var hit = HitTest(point);

        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit is not null)
        {
            SegmentSelected?.Invoke(this, hit);
        }
    }

    private Segment? HitTest(Point point)
    {
        var segments = Segments;
        if (segments is null || segments.Count == 0 || WindowEnd <= WindowStart)
        {
            return null;
        }

        const double leftPad = 10;
        const double rightPad = 10;
        const double topPad = 30;
        const double bottomPad = 24;

        double plotWidth = ActualWidth - leftPad - rightPad;
        double plotHeight = ActualHeight - topPad - bottomPad;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return null;
        }

        if (point.Y < topPad || point.Y > topPad + plotHeight || point.X < leftPad || point.X > leftPad + plotWidth)
        {
            return null;
        }

        double ratio = (point.X - leftPad) / plotWidth;
        long target = WindowStart + (long)(ratio * (WindowEnd - WindowStart));

        foreach (var segment in segments)
        {
            if (target >= segment.StartUtcMs && target < segment.EndUtcMs)
            {
                return segment;
            }
        }

        return null;
    }
}
