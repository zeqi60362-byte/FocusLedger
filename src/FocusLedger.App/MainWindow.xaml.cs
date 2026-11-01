using System.Windows;
using FocusLedger.App.Services;

namespace FocusLedger.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyTitleBar(this, ThemeService.CurrentTheme != AppTheme.Light);
    }
}
