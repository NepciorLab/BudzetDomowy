using System.Windows;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class PayeesWindow : Window
{
    public PayeesWindow(PayeesViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PayeesViewModel vm && vm.Selected is not null) vm.EditCommand.Execute(null);
    }
}
