using System.Windows;
using System.Windows.Input;
using BudzetDomowy.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public ICommand FocusAmountCommand { get; }
    public ICommand GoCommand { get; }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        FocusAmountCommand = new RelayCommand(() => QuickBar.FocusAmount());
        GoCommand = new RelayCommand<string>(key => { if (key is not null) vm.Navigate(key); });
        DataContext = vm;
        InitializeComponent();

        vm.SyncDetailsRequested += ShowSyncDetails;
        Loaded += (_, _) => QuickBar.FocusAmount();
    }

    private void ShowSyncDetails()
    {
        var window = new SyncDetailsWindow(new SyncDetailsViewModel(_vm.Core)) { Owner = this };
        window.ShowDialog();
    }
}
