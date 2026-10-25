using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class PaymentsView : UserControl
{
    public PaymentsView() => InitializeComponent();

    /// <summary>A double click on a payment opens its whole series for editing (the buttons in the row do their own thing).</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not PaymentsViewModel vm) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or DataGridColumnHeader or ScrollBar) return;
            if (d is DataGridRow { Item: PaymentItemViewModel item })
            {
                e.Handled = true;
                vm.EditCommand.Execute(item);
                return;
            }
        }
    }
}
