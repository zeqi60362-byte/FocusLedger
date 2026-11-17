using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusLedger.App.ViewModels;

namespace FocusLedger.App.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 点一下刚建的规则，绿点就消失。只处理还带着绿点的那条，
    /// 其他点击（改类别、勾启用、删）完全不拦。
    /// </summary>
    private void OnRuleRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row }
            && DataContext is RulesViewModel viewModel
            && row.IsNew)
        {
            viewModel.ClearNewCommand.Execute(row);
        }
    }
}
