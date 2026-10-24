using System.ComponentModel;
using System.Windows;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class ImportWindow : Window
{
    private readonly ImportViewModel _vm;

    public ImportWindow(ImportViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += Close;
        vm.PropertyChanged += OnVmChanged;
        UpdateButtons();
    }

    // the three action buttons share one place; only the one that fits the current step is shown
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportViewModel.Step)) UpdateButtons();
    }

    private void UpdateButtons()
    {
        NextButton.Visibility = _vm.Step < 4 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = _vm.Step == 4 ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.Visibility = _vm.Step == 5 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _vm.Step == 5 ? Visibility.Collapsed : Visibility.Visible;
    }
}
