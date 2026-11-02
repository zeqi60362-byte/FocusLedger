using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record WeekBlock(long Id, int DayIndex, int StartMin, int EndMin, string Title);

/// <summary>
/// 周视图：横轴周一到周日，纵轴 07:00–22:00。
/// 在某一列里按住往下拖即可选中那天的时段，松手抛出 RangeSelected(列, 起, 止)。
/// </summary>
public sealed class WeekCanvas : FrameworkElement
{
    public const int DayStart = 7 * 60;
    public const int DayEnd = 22 * 60;
    private const double GutterWidth = 46;
    private const int SnapMinutes = 5;

    public static readonly DependencyProperty PlansProperty = DependencyProperty.Register(
        nameof(Plans),
        typeof(IReadOnlyList<WeekBlock>),
        typeof(WeekCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private int? _day;
    private int? _anchor;
    private int? _cursor;

    public WeekCanvas()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public IReadOnlyList<WeekBlock>? Plans
    {
        get => (IReadOnlyList<WeekBlock>?)GetValue(PlansProperty);
        set => SetValue(PlansProperty, value);
    }

    public event EventHandler<(int Day, int Start, int End)>? RangeSelected;

    public event EventHandler<WeekBlock>? BlockClicked;

    public event EventHandler<WeekBlock>? BlockRightClicked;

    private double ColumnWidth => Math.Max(10, (ActualWidth - GutterWidth) / 7.0);

    private int DayAt(double x) => Math.Clamp((int)((x - GutterWidth) / ColumnWidth), 0, 6);

    private int MinuteAt(double y)
    {
        double usable = Math.Max(1, ActualHeight);
        double ratio = Math.Clamp(y / usable, 0, 1);
        int raw = (int)(DayStart + ratio * (DayEnd - DayStart));
        int snapped = (int)Math.Round(raw / (double)SnapMinutes) * SnapMinutes;
        return Math.Clamp(snapped, DayStart, DayEnd);
    }

    private double YOf(int minute) =>
        (minute - DayStart) / (double)(DayEnd - DayStart) * Math.Max(1, ActualHeight);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (point.X < GutterWidth)
        {
            return;
        }

        if (HitTest(point) is { } existing)
        {
            BlockClicked?.Invoke(this, existing);
            return;
        }

        _day = DayAt(point.X);
        _anchor = MinuteAt(point.Y);
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

        _cursor = MinuteAt(e.GetPosition(this).Y);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_day is not { } day || _anchor is not { } anchor || _cursor is not { } cursor)
        {
            return;
        }

        ReleaseMouseCapture();
        _day = null;
        _anchor = null;
        _cursor = null;
        InvalidateVisual();

        int start = Math.Min(anchor, cursor);
        int end = Math.Max(anchor, cursor);
        if (end - start < 25)
        {
            end = Math.Min(DayEnd, start + 60);
        }

        RangeSelected?.Invoke(this, (day, start, end));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } existing)
        {
            BlockRightClicked?.Invoke(this, existing);
        }
    }

    private WeekBlock? HitTest(Point point)
    {
        if (point.X < GutterWidth || Plans is null)
        {
            return null;
        }

        int day = DayAt(point.X);
        int minute = MinuteAt(point.Y);
        return Plans.FirstOrDefault(p => p.DayIndex == day && minute >= p.StartMin && minute < p.EndMin);
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
        var labelBrush = Res("TextDim", Colors.Gray);
        var textBrush = Res("Text", Colors.Black);
        var gridPen = new Pen(Res("TimelineGrid", Color.FromArgb(0x22, 0, 0, 0)), 1);

        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        for (int i = 0; i < 7; i++)
        {
            double x = GutterWidth + ColumnWidth * i + ColumnWidth / 2;
            var text = new FormattedText(names[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11.5, labelBrush, dpi);
            dc.DrawText(text, new Point(x - text.Width / 2, 2));
        }

        for (int minute = DayStart; minute <= DayEnd; minute += 60)
        {
            double y = YOf(minute);
            dc.DrawLine(gridPen, new Point(GutterWidth, y), new Point(width, y));

            var text = new FormattedText($"{minute / 60:00}:00", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(text, new Point(0, y - text.Height / 2));
        }

        for (int i = 0; i <= 7; i++)
        {
            double x = GutterWidth + ColumnWidth * i;
            dc.DrawLine(gridPen, new Point(x, 16), new Point(x, YOf(DayEnd)));
        }

        var accent = Res("StateActive", Color.FromRgb(0x2C, 0x7A, 0x52));
        var soft = new SolidColorBrush(((SolidColorBrush)accent).Color) { Opacity = 0.16 };

        if (Plans is not null)
        {
            foreach (var plan in Plans)
            {
                double x = GutterWidth + ColumnWidth * plan.DayIndex;
                double top = YOf(plan.StartMin);
                double bottom = YOf(plan.EndMin);
                var rect = new Rect(x + 2, top + 1, Math.Max(6, ColumnWidth - 4), Math.Max(5, bottom - top - 2));

                dc.DrawRoundedRectangle(soft, null, rect, 5, 5);
                dc.DrawRoundedRectangle(accent, null, new Rect(rect.X, rect.Y, 2.5, rect.Height), 1, 1);

                var title = new FormattedText(plan.Title ?? string.Empty, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 10.5, textBrush, dpi);
                title.MaxTextWidth = Math.Max(10, rect.Width - 8);
                title.Trimming = TextTrimming.CharacterEllipsis;
                if (rect.Height > title.Height + 4)
                {
                    dc.DrawText(title, new Point(rect.X + 6, rect.Y + 2));
                }
            }
        }

        if (_day is { } day && _anchor is { } anchor && _cursor is { } cursor)
        {
            double x = GutterWidth + ColumnWidth * day;
            double top = YOf(Math.Min(anchor, cursor));
            double bottom = YOf(Math.Max(anchor, cursor));
            var highlight = Res("StateSilent", Color.FromRgb(0x3B, 0x6E, 0xA5));
            var fill = new SolidColorBrush(((SolidColorBrush)highlight).Color) { Opacity = 0.25 };

            dc.DrawRoundedRectangle(fill, new Pen(highlight, 1),
                new Rect(x + 2, top, Math.Max(6, ColumnWidth - 4), Math.Max(2, bottom - top)), 5, 5);
        }
    }

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
