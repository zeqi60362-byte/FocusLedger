using System.Windows;
using System.Windows.Media;

namespace FocusLedger.App.Controls;

public sealed record BarSlice(double Value, Brush Brush);

/// <summary>按比例横向堆叠的条，用于"实证 vs 差额"和"差额去哪了"。</summary>
public sealed class ProportionBar : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IReadOnlyList<BarSlice>),
        typeof(ProportionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(ProportionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarSlice>? Slices
    {
        get => (IReadOnlyList<BarSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var track = Track ?? new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
        var shape = new RectangleGeometry(new Rect(0, 0, width, height), height / 2, height / 2);
        dc.DrawGeometry(track, null, shape);

        var slices = Slices;
        if (slices is null || slices.Count == 0)
        {
            return;
        }

        double total = slices.Sum(s => s.Value);
        if (total <= 0)
        {
            return;
        }

        dc.PushClip(shape);
        double x = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            if (slice.Value <= 0)
            {
                continue;
            }

            double w = i == slices.Count - 1
                ? width - x
                : Math.Max(0, slice.Value / total * width);

            dc.DrawRectangle(slice.Brush, null, new Rect(x, 0, w, height));
            x += w;
        }

        dc.Pop();
    }
}
