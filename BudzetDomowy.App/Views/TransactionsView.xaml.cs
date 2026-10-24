using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BudzetDomowy.App.ViewModels;
using ModernWpf.Controls;

namespace BudzetDomowy.App.Views;

public partial class TransactionsView : UserControl
{
    private TransactionsViewModel? Vm => DataContext as TransactionsViewModel;

    public TransactionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TransactionsViewModel old) old.SelectionClearRequested -= ClearSelection;
            if (e.NewValue is TransactionsViewModel vm) vm.SelectionClearRequested += ClearSelection;
        };
    }

    private void ClearSelection() => Grid.UnselectAll();

    private void OnLoaded(object sender, RoutedEventArgs e) => Vm?.Reload();

    private void Grid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e) => Vm?.SetEditing(true);

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Let the commit finish (it saves the change) before rows are refreshed.
        Dispatcher.BeginInvoke(() => Vm?.SetEditing(false), DispatcherPriority.Background);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(Grid.SelectedItems.OfType<TransactionRowViewModel>());

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || IsEditing()) return;
        switch (e.Key)
        {
            case Key.Delete:
                vm.DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape when Grid.SelectedItems.Count > 0:
                Grid.UnselectAll();                       // Esc deselects
                e.Handled = true;
                break;
            case Key.K when (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Grid.SelectedItems.Count > 0:
                ShowCategoryFlyout();                     // Ctrl+K changes the category of the selected rows
                e.Handled = true;
                break;
        }
    }

    private bool IsEditing() => Keyboard.FocusedElement is TextBox or ComboBox or DatePicker
                                || (Keyboard.FocusedElement as DependencyObject)?.GetType().Name == "DatePickerTextBox";

    private void MenuDelete_Click(object sender, RoutedEventArgs e) => Vm?.DeleteSelected();

    private void MenuChangeCategory_Click(object sender, RoutedEventArgs e) => ShowCategoryFlyout();

    private void ShowCategoryFlyout() => BulkFlyout.ShowAt(FlyoutAnchor);

    private void BulkFlyout_Opening(object? sender, object e)
    {
        if (Vm is null) return;
        BulkCombo.Choices = Vm.FilterCategories.Where(o => !o.Category.IsArchived).ToList();
        BulkCombo.Reset();
    }

    private void BulkCancel_Click(object sender, RoutedEventArgs e) => BulkFlyout.Hide();

    private void BulkApply_Click(object sender, RoutedEventArgs e)
    {
        BulkCombo.Commit();
        if (BulkCombo.SelectedValue is Guid id) Vm?.ChangeCategoryOfSelected(id);
        BulkFlyout.Hide();
    }
}
