using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record FlowSlice(string From, string To, int Count, Brush Brush);

/// <summary>
/// 简易桑基图：左边是来源应用，右边是去向应用，带宽 = 切换次数。
/// 只做两列，不做多级分层——这个场景里多级没有意义。
/// </summary>
public sealed class SankeyChart : FrameworkElement
{
    public static readonly DependencyProperty FlowsProperty = DependencyProperty.Register(
        nameof(Flows),
        typeof(IReadOnlyList<FlowSlice>),
        typeof(SankeyChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FlowSlice>? Flows
    {
        get => (IReadOnlyList<FlowSlice>?)GetValue(FlowsProperty);
        set => SetValue(FlowsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var flows = Flows;
        if (flows is null || flows.Count == 0)
        {
            DrawCentered(dc, "—", width, height);
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        var labelBrush = Res("Text", Colors.Black);
        var dimBrush = Res("TextDim", Colors.Gray);

        const double labelWidth = 122;
        const double nodeWidth = 9;
        const double nodeGap = 7;

        double x0 = labelWidth;
        double x1 = width - labelWidth;
        if (x1 <= x0 + 40)
        {
            x1 = x0 + 40;
        }

        // 左右两侧各自的节点总量
        var sources = flows
            .GroupBy(f => f.From)
            .Select(g => (Name: g.Key, Total: g.Sum(x => x.Count)))
            .OrderByDescending(x => x.Total)
            .ToList();

        var targets = flows
            .GroupBy(f => f.To)
            .Select(g => (Name: g.Key, Total: g.Sum(x => x.Count)))
            .OrderByDescending(x => x.Total)
            .ToList();

        double sourceTotal = sources.Sum(s => s.Total);
        double targetTotal = targets.Sum(t => t.Total);
        double usable = height - nodeGap * (Math.Max(sources.Count, targets.Count) - 1);
        if (usable <= 10)
        {
            return;
        }

        double sourceScale = (usable - nodeGap * (sources.Count - 1)) / sourceTotal;
        double targetScale = (usable - nodeGap * (targets.Count - 1)) / targetTotal;

        // 记录每个节点当前的纵向游标
        var sourceCursor = new Dictionary<string, double>();
        var targetCursor = new Dictionary<string, double>();
        var sourcePos = new Dictionary<string, (double Top, double Height)>();
        var targetPos = new Dictionary<string, (double Top, double Height)>();

        double y = 0;
        foreach (var s in sources)
        {
            double h = s.Total * sourceScale;
            sourcePos[s.Name] = (y, h);
            sourceCursor[s.Name] = y;
            y += h + nodeGap;
        }

        y = 0;
        foreach (var t in targets)
        {
            double h = t.Total * targetScale;
            targetPos[t.Name] = (y, h);
            targetCursor[t.Name] = y;
            y += h + nodeGap;
        }

        // 先画带，再画节点，最后画文字
        var ordered = flows
            .OrderByDescending(f => f.Count)
            .ToList();

        foreach (var flow in ordered)
        {
            if (!sourcePos.TryGetValue(flow.From, out var sp) || !targetPos.TryGetValue(flow.To, out var tp))
            {
                continue;
            }

            double sy = sourceCursor[flow.From];
            double ty = targetCursor[flow.To];
            double sh = Math.Max(1.5, flow.Count * sourceScale);
            double th = Math.Max(1.5, flow.Count * targetScale);

            sourceCursor[flow.From] = sy + sh;
            targetCursor[flow.To] = ty + th;

            double mid = x0 + (x1 - x0) / 2;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x0, sy), true, true);
                ctx.BezierTo(new Point(mid, sy), new Point(mid, ty), new Point(x1, ty), true, false);
                ctx.LineTo(new Point(x1, ty + th), true, false);
                ctx.BezierTo(new Point(mid, ty + th), new Point(mid, sy + sh), new Point(x0, sy + sh), true, false);
            }

            geometry.Freeze();
            dc.PushOpacity(0.42);
            dc.DrawGeometry(flow.Brush, null, geometry);
            dc.Pop();
        }

        foreach (var s in sources)
        {
            var (top, h) = sourcePos[s.Name];
            dc.DrawRoundedRectangle(Res("TextDim", Colors.Gray), null,
                new Rect(x0, top, nodeWidth, Math.Max(2, h)), 2, 2);

            var text = MakeText(s.Name, 11.5, typeface, labelBrush, dpi);
            dc.DrawText(text, new Point(x0 - 10 - text.Width, top + Math.Max(0, (h - text.Height) / 2)));
        }

        foreach (var t in targets)
        {
            var (top, h) = targetPos[t.Name];
            dc.DrawRoundedRectangle(Res("TextDim", Colors.Gray), null,
                new Rect(x1, top, nodeWidth, Math.Max(2, h)), 2, 2);

            var text = MakeText(t.Name, 11.5, typeface, dimBrush, dpi);
            dc.DrawText(text, new Point(x1 + nodeWidth + 10, top + Math.Max(0, (h - text.Height) / 2)));
        }
    }

    private static FormattedText MakeText(string value, double size, Typeface typeface, Brush brush, double dpi) =>
        new(value ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);

    private void DrawCentered(DrawingContext dc, string message, double width, double height)
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 12, Res("TextDim", Colors.Gray), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
