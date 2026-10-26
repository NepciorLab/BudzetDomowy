using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private ChartsViewModel? Charts => (DataContext as ReportsViewModel)?.Charts;

    /// <summary>"Szybki wybór" opens its menu under the button.</summary>
    private void ChartQuick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void QuickExpenses5_Click(object sender, RoutedEventArgs e) => Charts?.AddTop(5, TransactionType.Expense);

    private void QuickExpenses10_Click(object sender, RoutedEventArgs e) => Charts?.AddTop(10, TransactionType.Expense);

    private void QuickIncome_Click(object sender, RoutedEventArgs e) => Charts?.AddTop(5, TransactionType.Income);

    private void QuickAllExpenses_Click(object sender, RoutedEventArgs e) => Charts?.AddTotal(TransactionType.Expense);

    private void QuickAllIncome_Click(object sender, RoutedEventArgs e) => Charts?.AddTotal(TransactionType.Income);
}
