using BudzetDomowy.App.Infrastructure;
using System.Windows;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SyncDetailsWindow : Window
{
    public SyncDetailsWindow(SyncDetailsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        Closed += (_, _) => vm.Detach();
    }
}
