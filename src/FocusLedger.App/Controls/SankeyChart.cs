using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record FlowSlice(string From, string To, int Count, Brush SourceBrush, Brush TargetBrush);

/// <summary>
/// 简易桑基图：左边是来源应用，右边是去向应用，带宽 ~ 切换次数。
/// 节点用该应用自己的颜色，带子用来源色的半透明版本，保证"同色系、不同强度"。
/// </summary>
public sealed class SankeyChart : FrameworkElement
{
    private const double LabelWidth = 122;
    private const double NodeWidth = 10;
    private const double NodeGap = 9;
    private const double MinNodeHeight = 22;

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

        double x0 = LabelWidth;
        double x1 = Math.Max(x0 + 40, width - LabelWidth);

        var sources = flows
            .GroupBy(f => f.From)
            .Select(g => (Name: g.Key, Total: g.Sum(x => x.Count), Brush: g.First().SourceBrush))
            .OrderByDescending(x => x.Total)
            .ToList();

        var targets = flows
            .GroupBy(f => f.To)
            .Select(g => (Name: g.Key, Total: g.Sum(x => x.Count), Brush: g.First().TargetBrush))
            .OrderByDescending(x => x.Total)
            .ToList();

        var sourceHeight = Layout(sources.Select(s => (s.Name, (double)s.Total)).ToList(), height);
        var targetHeight = Layout(targets.Select(t => (t.Name, (double)t.Total)).ToList(), height);

        double usable = height - NodeGap * (Math.Max(sources.Count, targets.Count) - 1);
        if (usable <= 20)
        {
            return;
        }

        // 节点位置
        var sourcePos = new Dictionary<string, (double Top, double Height)>();
        double y = 0;
        foreach (var s in sources)
        {
            double h = sourceHeight[s.Name];
            sourcePos[s.Name] = (y, h);
            y += h + NodeGap;
        }

        var targetPos = new Dictionary<string, (double Top, double Height)>();
        y = 0;
        foreach (var t in targets)
        {
            double h = targetHeight[t.Name];
            targetPos[t.Name] = (y, h);
            y += h + NodeGap;
        }

        var sourceCursor = sourcePos.ToDictionary(kv => kv.Key, kv => kv.Value.Top);
        var targetCursor = targetPos.ToDictionary(kv => kv.Key, kv => kv.Value.Top);

        // 带子：按来源节点内的占比分配厚度，保证正好填满节点
        foreach (var flow in flows.OrderByDescending(f => f.Count))
        {
            if (!sourcePos.TryGetValue(flow.From, out var sp) ||
                !targetPos.TryGetValue(flow.To, out var tp))
            {
                continue;
            }

            double sourceTotal = sources.First(s => s.Name == flow.From).Total;
            double targetTotal = targets.First(t => t.Name == flow.To).Total;

            double sh = Math.Max(2, sp.Height * (flow.Count / sourceTotal) - 1.2);
            double th = Math.Max(2, tp.Height * (flow.Count / targetTotal) - 1.2);

            double sy = sourceCursor[flow.From];
            double ty = Math.Min(targetCursor[flow.To], tp.Top + tp.Height - th);

            sourceCursor[flow.From] = sy + sh + 1.2;
            targetCursor[flow.To] = ty + th + 1.2;

            double mid = x0 + (x1 - x0) / 2;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x0 + NodeWidth, sy), true, true);
                ctx.BezierTo(new Point(mid, sy), new Point(mid, ty), new Point(x1, ty), true, false);
                ctx.LineTo(new Point(x1, ty + th), true, false);
                ctx.BezierTo(new Point(mid, ty + th), new Point(mid, sy + sh), new Point(x0 + NodeWidth, sy + sh), true, false);
            }

            geometry.Freeze();
            dc.PushOpacity(0.38);
            dc.DrawGeometry(flow.SourceBrush, null, geometry);
            dc.Pop();
        }

        // 节点：用应用自己的颜色，实心
        foreach (var s in sources)
        {
            var (top, h) = sourcePos[s.Name];
            dc.DrawRoundedRectangle(s.Brush, null, new Rect(x0, top, NodeWidth, h), 3, 3);

            var text = MakeText(s.Name, 11.5, typeface, labelBrush, dpi);
            dc.DrawText(text, new Point(x0 - 10 - text.Width, top + Math.Max(0, (h - text.Height) / 2)));
        }

        foreach (var t in targets)
        {
            var (top, h) = targetPos[t.Name];
            dc.DrawRoundedRectangle(t.Brush, null, new Rect(x1, top, NodeWidth, h), 3, 3);

            var text = MakeText(t.Name, 11.5, typeface, labelBrush, dpi);
            dc.DrawText(text, new Point(x1 + NodeWidth + 10, top + Math.Max(0, (h - text.Height) / 2)));
        }
    }

    /// <summary>
    /// 分配节点高度：按总量线性分配，但每个节点给一个最小高度，
    /// 差额从大节点里按比例扣，避免小应用被压成一条线。
    /// </summary>
    private static Dictionary<string, double> Layout(IReadOnlyList<(string Name, double Total)> nodes, double height)
    {
        var result = new Dictionary<string, double>();
        double available = height - NodeGap * (nodes.Count - 1);
        if (available <= 0 || nodes.Count == 0)
        {
            return result;
        }

        double sum = nodes.Sum(n => n.Total);
        if (sum <= 0)
        {
            return result;
        }

        var heights = nodes.ToDictionary(n => n.Name, n => n.Total / sum * available);

        double deficit = 0;
        foreach (var node in nodes)
        {
            if (heights[node.Name] < MinNodeHeight)
            {
                deficit += MinNodeHeight - heights[node.Name];
                heights[node.Name] = MinNodeHeight;
            }
        }

        double aboveSum = nodes.Where(n => heights[n.Name] > MinNodeHeight).Sum(n => heights[n.Name]);
        if (deficit > 0 && aboveSum > MinNodeHeight)
        {
            foreach (var node in nodes)
            {
                if (heights[node.Name] > MinNodeHeight)
                {
                    heights[node.Name] -= deficit * (heights[node.Name] / aboveSum);
                }
            }
        }

        foreach (var node in nodes)
        {
            result[node.Name] = Math.Max(MinNodeHeight / 2, heights[node.Name]);
        }

        return result;
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
