using System.Windows;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public ICommand FocusAmountCommand { get; }
    public ICommand GoCommand { get; }
    public ICommand FocusCurrencyCommand { get; }
    public ICommand FocusRateCommand { get; }
    public ICommand ShowShortcutsCommand { get; }

    public MainWindow(MainViewModel vm, SettingsStore store)
    {
        _vm = vm;
        FocusAmountCommand = new RelayCommand(() => QuickBar.FocusAmount());
        FocusCurrencyCommand = new RelayCommand(() => QuickBar.FocusCurrency());
        FocusRateCommand = new RelayCommand(() => QuickBar.FocusRate());
        ShowShortcutsCommand = new RelayCommand(() =>
        {
            vm.Navigate("settings");
            vm.Settings.SelectedTab = SettingsViewModel.ShortcutsTab;
        });
        GoCommand = new RelayCommand<string>(key => { if (key is not null) vm.Navigate(key); });
        DataContext = vm;
        InitializeComponent();

        WindowPlacement.Apply(this, store.Current);
        Closing += (_, _) => store.Update(s => WindowPlacement.Capture(this, s));
        vm.SyncDetailsRequested += ShowSyncDetails;
        Loaded += (_, _) => QuickBar.FocusAmount();
    }

    private void ShowSyncDetails()
    {
        var window = new SyncDetailsWindow(new SyncDetailsViewModel(_vm.Core)) { Owner = this };
        window.ShowDialog();
    }
}
