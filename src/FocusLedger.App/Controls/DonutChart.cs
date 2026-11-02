using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

/// <summary>环形占比图：中间放一个主数字，外圈按比例分色。</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IReadOnlyList<BarSlice>),
        typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterValueProperty = DependencyProperty.Register(
        nameof(CenterValue),
        typeof(string),
        typeof(DonutChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterLabelProperty = DependencyProperty.Register(
        nameof(CenterLabel),
        typeof(string),
        typeof(DonutChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarSlice>? Slices
    {
        get => (IReadOnlyList<BarSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public string CenterValue
    {
        get => (string)GetValue(CenterValueProperty);
        set => SetValue(CenterValueProperty, value);
    }

    public string CenterLabel
    {
        get => (string)GetValue(CenterLabelProperty);
        set => SetValue(CenterLabelProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = Math.Min(width, height) * 0.16;
        double radius = Math.Min(width, height) / 2 - thickness / 2 - 1;
        if (radius <= 1)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var slices = Slices;
        double total = slices?.Sum(s => s.Value) ?? 0;

        if (slices is null || total <= 0)
        {
            var emptyPen = new Pen(Res("TrackBg", Color.FromRgb(0x23, 0x23, 0x29)), thickness);
            dc.DrawEllipse(null, emptyPen, center, radius, radius);
            return;
        }

        // 轨道
        var trackPen = new Pen(Res("TrackBg", Color.FromRgb(0x23, 0x23, 0x29)), thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        double angle = -90;
        foreach (var slice in slices)
        {
            if (slice.Value <= 0)
            {
                continue;
            }

            double sweep = slice.Value / total * 360.0;
            var pen = new Pen(slice.Brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };

            if (sweep >= 359.5)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(PointOn(center, radius, angle), false, false);
                ctx.ArcTo(
                    PointOn(center, radius, angle + sweep),
                    new Size(radius, radius),
                    0,
                    sweep > 180,
                    SweepDirection.Clockwise,
                    true,
                    false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            angle += sweep;
        }

        // 中间的数字
        double valueSize = radius * 0.52;
        var valueText = new FormattedText(
            CenterValue, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            valueSize, Res("Text", Colors.White), dpi);

        double labelSize = Math.Max(10, radius * 0.17);
        var labelText = new FormattedText(
            CenterLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, labelSize, Res("TextDim", Color.FromRgb(0x8A, 0x8A, 0x94)), dpi);

        double block = valueText.Height + 4 + labelText.Height;
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - block / 2));
        dc.DrawText(labelText, new Point(center.X - labelText.Width / 2, center.Y - block / 2 + valueText.Height + 4));
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
