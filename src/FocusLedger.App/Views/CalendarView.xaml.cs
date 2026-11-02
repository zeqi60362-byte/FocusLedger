using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusLedger.App.Controls;

namespace FocusLedger.App.Views;

public partial class CalendarView : UserControl
{
    public CalendarView()
    {
        InitializeComponent();
        EditorOverlay.IsVisibleChanged += OnEditorVisibleChanged;
    }

    private void OnRangeSelected(object? sender, (int Day, int Start, int End) e)
        => (DataContext as ViewModels.CalendarViewModel)?.OnRangeSelected(e.Day, e.Start, e.End);

    private void OnBlockClicked(object? sender, WeekBlock e)
        => (DataContext as ViewModels.CalendarViewModel)?.OnBlockClicked(e);

    /// <summary>弹窗一出现就把光标放进标题框，少点一次鼠标。</summary>
    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(new Action(() => TitleBox.Focus()), DispatcherPriority.Input);
        }
    }
}
