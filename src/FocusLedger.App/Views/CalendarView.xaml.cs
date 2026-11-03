using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FocusLedger.App.Controls;

namespace FocusLedger.App.Views;

public partial class CalendarView : UserControl
{
    /// <summary>周视图卡片的可调范围。下限要保证 07:00–23:00 的刻度还分得开。</summary>
    private const double MinWeekHeight = 320;
    private const double MaxWeekHeight = 1400;

    public CalendarView()
    {
        InitializeComponent();
        EditorOverlay.IsVisibleChanged += OnEditorVisibleChanged;
    }

    private void OnRangeSelected(object? sender, (int Day, int Start, int End) e)
        => (DataContext as ViewModels.CalendarViewModel)?.OnRangeSelected(e.Day, e.Start, e.End);

    private void OnBlockClicked(object? sender, WeekBlock e)
        => (DataContext as ViewModels.CalendarViewModel)?.OnBlockClicked(e);

    /// <summary>
    /// 拖拽把手直接改周视图卡片的高度。
    /// 不走 GridSplitter 的「分剩余空间」那套——外层是 ScrollViewer，内容高度按内容算，
    /// 星号行在这里没有意义，只有具体数字才拖得动。
    /// </summary>
    private void OnSplitterDrag(object sender, DragDeltaEventArgs e)
    {
        double current = double.IsNaN(WeekCard.Height) ? WeekCard.ActualHeight : WeekCard.Height;
        double next = Math.Clamp(current + e.VerticalChange, MinWeekHeight, MaxWeekHeight);

        if (Math.Abs(next - current) > 0.5)
        {
            WeekCard.Height = next;
        }
    }

    /// <summary>弹窗一出现就把光标放进标题框，少点一次鼠标。</summary>
    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(new Action(() => TitleBox.Focus()), DispatcherPriority.Input);
        }
    }
}
