using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class RecurringView : UserControl
{
    public RecurringView() => InitializeComponent();

    private void Expense_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecurringViewModel vm) vm.EditIsIncome = false;
    }

    private void Income_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecurringViewModel vm) vm.EditIsIncome = true;
    }
}
