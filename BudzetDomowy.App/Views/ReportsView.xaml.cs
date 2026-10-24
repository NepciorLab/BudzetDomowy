using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private void MonthScope_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReportsViewModel vm) vm.IsYear = false;
    }

    private void YearScope_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReportsViewModel vm) vm.IsYear = true;
    }
}
