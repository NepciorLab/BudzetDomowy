using System.Windows;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SyncDetailsWindow : Window
{
    public SyncDetailsWindow(SyncDetailsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Closed += (_, _) => vm.Detach();
    }
}
