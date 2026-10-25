using System.Windows;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

/// <summary>The QR code of a bill and the choice: paid now, scheduled in the bank, or neither (close).</summary>
public partial class PayWindow : Window
{
    public PayWindow(PaymentPayViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.Closed += () => DialogResult = vm.Result != PayResult.None;
    }
}
