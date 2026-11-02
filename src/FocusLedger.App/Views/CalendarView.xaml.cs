using System.Windows.Controls;

namespace FocusLedger.App.Views;

public partial class CalendarView : UserControl
{
    public CalendarView()
    {
        InitializeComponent();
    }
    private void OnRangeSelected(object? sender, (int Day, int Start, int End) e)
        => (DataContext as ViewModels.CalendarViewModel)?.OnRangeSelected(e.Day, e.Start, e.End);
}
