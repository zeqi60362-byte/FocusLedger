using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record PlanBlock(long Id, int StartMin, int EndMin, string Title, string Location, string Url, bool Remind);

/// <summary>
/// 计划时间画布：07:00–23:00 一屏放下，按住左键往下拖即可选一段时间。
/// 松手时抛出 RangeSelected，由页面弹窗补标题等信息。
/// </summary>
public sealed class PlanCanvas : FrameworkElement
{
    public const int DayStart = 7 * 60;
    public const int DayEnd = 23 * 60;
    private const double GutterWidth = 46;
    private const int SnapMinutes = 15;

    public static readonly DependencyProperty PlansProperty = DependencyProperty.Register(
        nameof(Plans),
        typeof(IReadOnlyList<PlanBlock>),
        typeof(PlanCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private int? _anchor;
    private int? _cursor;

    public PlanCanvas()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public IReadOnlyList<PlanBlock>? Plans
    {
        get => (IReadOnlyList<PlanBlock>?)GetValue(PlansProperty);
        set => SetValue(PlansProperty, value);
    }

    /// <summary>拖拽结束：(开始分钟, 结束分钟)。</summary>
    public event EventHandler<(int Start, int End)>? RangeSelected;

    /// <summary>点中已有的计划。</summary>
    public event EventHandler<PlanBlock>? PlanClicked;

    public event EventHandler<PlanBlock>? PlanRightClicked;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);

        if (HitTest(point) is { } existing)
        {
            PlanClicked?.Invoke(this, existing);
            return;
        }

        _anchor = ToMinutes(point.Y);
        _cursor = _anchor;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_anchor is null)
        {
            return;
        }

        _cursor = ToMinutes(e.GetPosition(this).Y);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_anchor is not { } anchor || _cursor is not { } cursor)
        {
            return;
        }

        ReleaseMouseCapture();
        _anchor = null;
        _cursor = null;
        InvalidateVisual();

        int start = Math.Min(anchor, cursor);
        int end = Math.Max(anchor, cursor);
        if (end - start < SnapMinutes)
        {
            end = Math.Min(DayEnd, start + 60);
        }

        RangeSelected?.Invoke(this, (start, end));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } existing)
        {
            PlanRightClicked?.Invoke(this, existing);
        }
    }

    private PlanBlock? HitTest(Point point)
    {
        if (point.X < GutterWidth || Plans is null)
        {
            return null;
        }

        int minute = ToMinutes(point.Y);
        return Plans.FirstOrDefault(p => minute >= p.StartMin && minute < p.EndMin);
    }

    private int ToMinutes(double y)
    {
        double usable = Math.Max(1, ActualHeight);
        double ratio = Math.Clamp(y / usable, 0, 1);
        int raw = (int)(DayStart + ratio * (DayEnd - DayStart));
        int snapped = (int)Math.Round(raw / (double)SnapMinutes) * SnapMinutes;
        return Math.Clamp(snapped, DayStart, DayEnd);
    }

    private double ToY(int minute)
    {
        double usable = Math.Max(1, ActualHeight);
        return (minute - DayStart) / (double)(DayEnd - DayStart) * usable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

        var gridPen = new Pen(Res("TimelineGrid", Color.FromArgb(0x22, 0, 0, 0)), 1);
        var halfPen = new Pen(Res("TimelineGrid", Color.FromArgb(0x11, 0, 0, 0)), 1);
        var labelBrush = Res("TextDim", Colors.Gray);

        // 网格 + 整点标签
        for (int minute = DayStart; minute <= DayEnd; minute += 30)
        {
            double y = ToY(minute);
            bool whole = minute % 60 == 0;
            dc.DrawLine(whole ? gridPen : halfPen, new Point(GutterWidth, y), new Point(width, y));

            if (whole)
            {
                var text = new FormattedText($"{minute / 60:00}:00", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 10.5, labelBrush, dpi);
                dc.DrawText(text, new Point(0, y - text.Height / 2));
            }
        }

        // 已有计划
        if (Plans is not null)
        {
            var brush = Res("StateActive", Color.FromRgb(0x2C, 0x7A, 0x52));
            var soft = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = 0.16 };

            foreach (var plan in Plans)
            {
                double top = ToY(plan.StartMin);
                double bottom = ToY(plan.EndMin);
                var rect = new Rect(GutterWidth + 2, top + 1, Math.Max(10, width - GutterWidth - 8), Math.Max(6, bottom - top - 2));

                dc.DrawRoundedRectangle(soft, null, rect, 7, 7);
                dc.DrawRoundedRectangle(brush, null, new Rect(rect.X, rect.Y, 3, rect.Height), 1.5, 1.5);

                string label = string.IsNullOrWhiteSpace(plan.Location)
                    ? plan.Title
                    : $"{plan.Title} · {plan.Location}";
                var title = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 11.5, Res("Text", Colors.Black), dpi);
                title.MaxTextWidth = Math.Max(20, rect.Width - 14);
                title.Trimming = TextTrimming.CharacterEllipsis;

                if (rect.Height > title.Height + 6)
                {
                    dc.DrawText(title, new Point(rect.X + 10, rect.Y + 4));
                }
            }
        }

        // 拖拽中的选区
        if (_anchor is { } anchor && _cursor is { } cursor)
        {
            double top = ToY(Math.Min(anchor, cursor));
            double bottom = ToY(Math.Max(anchor, cursor));
            var selBrush = Res("StateSilent", Color.FromRgb(0x3B, 0x6E, 0xA5));
            var fill = new SolidColorBrush(((SolidColorBrush)selBrush).Color) { Opacity = 0.22 };

            var rect = new Rect(GutterWidth + 2, top, Math.Max(10, width - GutterWidth - 8), Math.Max(2, bottom - top));
            dc.DrawRoundedRectangle(fill, new Pen(selBrush, 1), rect, 6, 6);

            int start = Math.Min(anchor, cursor);
            int end = Math.Max(anchor, cursor);
            var hint = new FormattedText($"{start / 60:00}:{start % 60:00} – {end / 60:00}:{end % 60:00}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11,
                Res("Text", Colors.Black), dpi);
            dc.DrawText(hint, new Point(rect.X + 8, Math.Max(0, top - hint.Height - 2)));
        }
    }

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
