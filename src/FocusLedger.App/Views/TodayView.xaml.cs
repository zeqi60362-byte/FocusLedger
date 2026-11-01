using System.Windows.Controls;
using FocusLedger.App.ViewModels;
using FocusLedger.Core;

namespace FocusLedger.App.Views;

public partial class TodayView : UserControl
{
    public TodayView()
    {
        InitializeComponent();
    }

    private void OnSegmentSelected(object? sender, Segment segment)
    {
        if (DataContext is TodayViewModel viewModel)
        {
            viewModel.ShowDetail(segment);
        }
    }
}
