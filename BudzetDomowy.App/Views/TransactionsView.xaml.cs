using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BudzetDomowy.App.Infrastructure;
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
        PreviewKeyDown += View_PreviewKeyDown;
    }

    private void ClearSelection() => Grid.UnselectAll();

    /// <summary>Esc: first deselects the selected rows, then clears the filters (not while a cell or a drop-down list is being used).</summary>
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { } vm) return;
        if (Grid.IsKeyboardFocusWithin && IsEditing()) return;
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;

        if (Grid.SelectedItems.Count > 0) { Grid.UnselectAll(); e.Handled = true; }
        else if (vm.ClearFiltersIfAny()) e.Handled = true;
    }

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
        if (ShortcutMap.Instance.Matches(ShortcutMap.TxDelete, e))
        {
            vm.DeleteSelected();
            e.Handled = true;
        }
        else if (ShortcutMap.Instance.Matches(ShortcutMap.TxChangeCategory, e) && Grid.SelectedItems.Count > 0)
        {
            ShowCategoryFlyout();                         // changes the category of the selected rows
            e.Handled = true;
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
