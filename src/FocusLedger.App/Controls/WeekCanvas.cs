using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record WeekBlock(
    long Id, int DayIndex, int StartMin, int EndMin, string Title, string Location, string Url);

/// <summary>
/// 周视图：横轴周一到周日，纵轴 07:00–23:00。
/// 在某一列里按住往下拖即可选中那天的时段，松手抛出 RangeSelected(列, 起, 止)。
/// </summary>
public sealed class WeekCanvas : FrameworkElement
{
    public const int DayStart = 7 * 60;
    public const int DayEnd = 23 * 60;

    private const double GutterWidth = 46;
    private const double HeaderHeight = 22;
    private const double BottomPad = 10;
    private const int SnapMinutes = 5;

    public static readonly DependencyProperty PlansProperty = DependencyProperty.Register(
        nameof(Plans),
        typeof(IReadOnlyList<WeekBlock>),
        typeof(WeekCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPlansChanged));

    public static readonly DependencyProperty TodayIndexProperty = DependencyProperty.Register(
        nameof(TodayIndex),
        typeof(int),
        typeof(WeekCanvas),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

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

    /// <summary>本周里"今天"是第几列；-1 表示不在本周。</summary>
    public int TodayIndex
    {
        get => (int)GetValue(TodayIndexProperty);
        set => SetValue(TodayIndexProperty, value);
    }

    public event EventHandler<(int Day, int Start, int End)>? RangeSelected;

    public event EventHandler<WeekBlock>? BlockClicked;

    public event EventHandler<WeekBlock>? BlockRightClicked;

    private double ColumnWidth => Math.Max(10, (ActualWidth - GutterWidth) / 7.0);

    /// <summary>真正画时间刻度的高度：扣掉表头和底部留白，否则 07:00 / 23:00 的字会被切掉一半。</summary>
    private double PlotHeight => Math.Max(40, ActualHeight - HeaderHeight - BottomPad);

    private double PlotBottom => HeaderHeight + PlotHeight;

    private int DayAt(double x) => Math.Clamp((int)((x - GutterWidth) / ColumnWidth), 0, 6);

    private int MinuteAt(double y)
    {
        double ratio = Math.Clamp((y - HeaderHeight) / PlotHeight, 0, 1);
        int raw = (int)(DayStart + ratio * (DayEnd - DayStart));
        int snapped = (int)Math.Round(raw / (double)SnapMinutes) * SnapMinutes;
        return Math.Clamp(snapped, DayStart, DayEnd);
    }

    private double YOf(int minute) =>
        HeaderHeight + (minute - DayStart) / (double)(DayEnd - DayStart) * PlotHeight;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (point.X < GutterWidth || point.Y < HeaderHeight)
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

    /// <summary>
    /// 绑定的是 ObservableCollection：增删计划时依赖属性本身不变，
    /// 不订阅 CollectionChanged 的话画布会一直停在打开页面那一刻的样子。
    /// </summary>
    private static void OnPlansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (WeekCanvas)d;

        if (e.OldValue is INotifyCollectionChanged previous)
        {
            previous.CollectionChanged -= canvas.OnPlansCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged current)
        {
            current.CollectionChanged += canvas.OnPlansCollectionChanged;
        }

        canvas.InvalidateVisual();
    }

    private void OnPlansCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    private WeekBlock? HitTest(Point point)
    {
        if (point.X < GutterWidth || point.Y < HeaderHeight || Plans is null)
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
        var typeface = new Typeface(new FontFamily("Noto Sans SC, Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

        // 先铺一层全透明矩形。
        // FrameworkElement 只在「画过像素」的地方可命中：不铺这一层的话，
        // 没有底纹的列（周一/三/四/五）空白处点下去会直接穿透到背后的卡片，
        // 表现为「只有今天和周末那两列能拖」。Transparent 参与命中测试，null 不参与。
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var labelBrush = Res("TextDim", Colors.Gray);
        var textBrush = Res("Text", Colors.Black);
        var accent = Res("StateActive", Color.FromRgb(0x2C, 0x7A, 0x52));
        var gridPen = new Pen(Res("TimelineGrid", Color.FromArgb(0x22, 0, 0, 0)), 1);
        var faintPen = new Pen(Res("TimelineGrid", Color.FromArgb(0x11, 0, 0, 0)), 1);

        // 周末底纹：周六、周日两列压一层极淡的灰，扫视时不用数格子
        var weekend = new SolidColorBrush(Color.FromArgb(0x0E, 0x80, 0x80, 0x80));
        for (int i = 5; i < 7; i++)
        {
            double wx = GutterWidth + ColumnWidth * i;
            dc.DrawRectangle(weekend, null, new Rect(wx, HeaderHeight, ColumnWidth, PlotHeight));
        }

        // 今天那一列重点标出来
        // 今天那一列重点标出来。底色用主题里的 AccentSoft——
        // 和月历里选中日的那格是同一个色，两处看起来才是一套。
        if (TodayIndex is >= 0 and <= 6)
        {
            var tint = Res("AccentSoft", Color.FromRgb(0xED, 0xF5, 0xF1));
            double tx = GutterWidth + ColumnWidth * TodayIndex;
            dc.DrawRectangle(tint, null, new Rect(tx, HeaderHeight, ColumnWidth, PlotHeight));
        }

        // 横向整点刻度
        for (int minute = DayStart; minute <= DayEnd; minute += 60)
        {
            double y = YOf(minute);
            dc.DrawLine(minute % 120 == 0 ? gridPen : faintPen,
                new Point(GutterWidth, y), new Point(width, y));

            var text = new FormattedText($"{minute / 60:00}:00", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);

            // 首尾两格的文字要往里收，不然会被画布边缘切掉
            double labelY = Math.Clamp(y - text.Height / 2, HeaderHeight - 4, height - text.Height);
            dc.DrawText(text, new Point(GutterWidth - 8 - text.Width, labelY));
        }

        // 纵向分隔线
        for (int i = 0; i <= 7; i++)
        {
            double x = GutterWidth + ColumnWidth * i;
            dc.DrawLine(gridPen, new Point(x, HeaderHeight), new Point(x, PlotBottom));
        }

        // 表头：周一到周日
        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        for (int i = 0; i < 7; i++)
        {
            bool isToday = i == TodayIndex;
            var tf = new Typeface(new FontFamily("Noto Sans SC, Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
                isToday ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(names[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                tf, 11.5, isToday ? accent : labelBrush, dpi);

            double x = GutterWidth + ColumnWidth * i + ColumnWidth / 2;
            dc.DrawText(text, new Point(x - text.Width / 2, HeaderHeight - text.Height - 4));
        }

        // 已有计划
        // 计划块必须是实色的：半透明底会让背后的横向刻度线透出来，
        // 一块 5 小时的计划看上去像被切成好几段。这里把 16% 的绿色和卡片底色
        // 预先混成一个不透明色，视觉效果和原来一样，但挡得住底层网格。
        var blockFill = new SolidColorBrush(BlendOverSurface(accent, 0.16));

        if (Plans is not null)
        {
            foreach (var plan in Plans)
            {
                double x = GutterWidth + ColumnWidth * plan.DayIndex;
                double top = YOf(plan.StartMin);
                double bottom = YOf(plan.EndMin);
                var rect = new Rect(x + 2, top + 1, Math.Max(6, ColumnWidth - 5), Math.Max(5, bottom - top - 2));

                dc.DrawRoundedRectangle(blockFill, null, rect, 5, 5);
                dc.DrawRoundedRectangle(accent, null, new Rect(rect.X, rect.Y, 2.5, rect.Height), 1, 1);

                // 标题 / 地点 / 网址各占一行，水平居中；放不下就少画几行，绝不画出块外。
                // 标题单独加大加粗：一周七列里它是唯一能用来认出这块是什么的信息。
                var lines = new List<(string Text, bool IsTitle)> { (plan.Title, true) };
                if (!string.IsNullOrWhiteSpace(plan.Location))
                {
                    lines.Add((plan.Location, false));
                }

                if (!string.IsNullOrWhiteSpace(plan.Url))
                {
                    lines.Add((ShortenUrl(plan.Url), false));
                }

                double textWidth = Math.Max(10, rect.Width - 6);
                var titleTypeface = new Typeface(typeface.FontFamily, FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal);

                FormattedText MakeLine(string text, bool isTitle) =>
                    new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        isTitle ? titleTypeface : typeface, isTitle ? 12 : 10.5, textBrush, dpi)
                    {
                        MaxTextWidth = textWidth,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center
                    };

                // 从标题往下逐行加，直到超出块高为止（标题永远留）
                var drawn = new List<(FormattedText Text, double Height)>();
                double total = 0;
                foreach (var (text, isTitle) in lines)
                {
                    var formatted = MakeLine(text, isTitle);
                    double step = formatted.Height + 1;

                    if (drawn.Count > 0 && total + step > rect.Height - 6)
                    {
                        break;
                    }

                    drawn.Add((formatted, step));
                    total += step;
                }

                // 整组文字在块内垂直居中
                double startY = rect.Y + Math.Max(2, (rect.Height - total) / 2);

                foreach (var (text, lineStep) in drawn)
                {
                    dc.DrawText(text, new Point(rect.X + 3, startY));
                    startY += lineStep;
                }
            }
        }

        // 拖拽中的选区，顺手把时间写在旁边
        if (_day is { } day && _anchor is { } anchor && _cursor is { } cursor)
        {
            double x = GutterWidth + ColumnWidth * day;
            double top = YOf(Math.Min(anchor, cursor));
            double bottom = YOf(Math.Max(anchor, cursor));
            var highlight = Res("StateSilent", Color.FromRgb(0x3B, 0x6E, 0xA5));
            var fill = new SolidColorBrush(((SolidColorBrush)highlight).Color) { Opacity = 0.25 };

            dc.DrawRoundedRectangle(fill, new Pen(highlight, 1),
                new Rect(x + 2, top, Math.Max(6, ColumnWidth - 5), Math.Max(2, bottom - top)), 5, 5);

            int start = Math.Min(anchor, cursor);
            int end = Math.Max(anchor, cursor);
            var hint = new FormattedText($"{start / 60:00}:{start % 60:00} – {end / 60:00}:{end % 60:00}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, textBrush, dpi);

            hint.MaxTextWidth = Math.Max(60, GutterWidth + ColumnWidth);
            double hintX = Math.Clamp(x + ColumnWidth + 4, GutterWidth, Math.Max(GutterWidth, width - hint.Width - 4));
            dc.DrawText(hint, new Point(hintX, Math.Max(HeaderHeight, top - hint.Height - 3)));
        }
    }

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>
    /// 一列只有 40–60 px，`https://` 白占 8 个字符，去掉协议和 www 前缀能多露出几个字。
    /// </summary>
    private static string ShortenUrl(string url)
    {
        string value = url.Trim();

        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        return value.TrimEnd('/');
    }

    /// <summary>
    /// 把半透明色和卡片底色预先混成一个不透明色：视觉上和「16% 透明绿」一致，
    /// 但画上去会把背后的刻度线挡住。
    /// </summary>
    private Color BlendOverSurface(Brush over, double alpha)
    {
        var surface = (Res("Surface", Colors.White) as SolidColorBrush)?.Color ?? Colors.White;
        var tint = (over as SolidColorBrush)?.Color ?? Color.FromRgb(0x2C, 0x7A, 0x52);

        return Color.FromRgb(
            (byte)Math.Round(surface.R * (1 - alpha) + tint.R * alpha),
            (byte)Math.Round(surface.G * (1 - alpha) + tint.G * alpha),
            (byte)Math.Round(surface.B * (1 - alpha) + tint.B * alpha));
    }
}
