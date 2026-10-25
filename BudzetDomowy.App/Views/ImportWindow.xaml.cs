using BudzetDomowy.App.Infrastructure;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        UiScaling.Attach(this);
        vm.CloseRequested += Close;
        vm.PropertyChanged += OnVmChanged;
        UpdateButtons();
    }

    /// <summary>A double click on a row that can be corrected opens the window for it.</summary>
    private void PreviewGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridColumnHeader) return;
            if (d is DataGridRow { Item: PlanRowView view })
            {
                _vm.SelectedPreviewRow = view;
                if (_vm.FixRowCommand.CanExecute(null)) _vm.FixRowCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
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
