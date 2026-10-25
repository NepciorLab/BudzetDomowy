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
        if (e.Key is Key.Home or Key.End && (Keyboard.Modifiers & ~(ModifierKeys.Shift | ModifierKeys.Control)) == 0)
        {
            GoToEdge(toEnd: e.Key == Key.End, extend: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);   // first / last row, not first / last column
            e.Handled = true;
        }
        else if (ShortcutMap.Instance.Matches(ShortcutMap.TxDelete, e))
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

    /// <summary>Home and End jump to the first and the last row (with Shift the selection grows up to it).</summary>
    private void GoToEdge(bool toEnd, bool extend)
    {
        if (Grid.Items.Count == 0) return;
        var target = Grid.Items[toEnd ? Grid.Items.Count - 1 : 0];
        var from = Grid.SelectedItem is { } current ? Grid.Items.IndexOf(current) : (toEnd ? 0 : Grid.Items.Count - 1);
        var to = Grid.Items.IndexOf(target);

        if (extend)
        {
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) Grid.SelectedItems.Add(Grid.Items[i]);
        }
        else
        {
            Grid.SelectedItems.Clear();
            Grid.SelectedItem = target;
        }
        Grid.ScrollIntoView(target);
        if (Grid.Columns.Count > 0) Grid.CurrentCell = new DataGridCellInfo(target, Grid.Columns.First(c => c.Visibility == Visibility.Visible));
        Grid.Focus();
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

    // ----- the same amount for all selected rows -----

    private void MenuChangeAmount_Click(object sender, RoutedEventArgs e) => AmountFlyout.ShowAt(AmountFlyoutAnchor);

    private void AmountFlyout_Opening(object? sender, object e)
    {
        AmountBox.Text = "";
        AmountError.Visibility = Visibility.Collapsed;
    }

    private void AmountFlyout_Opened(object? sender, object e) => AmountBox.Focus();

    private void AmountCancel_Click(object sender, RoutedEventArgs e) => AmountFlyout.Hide();

    private void AmountBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AmountApply_Click(sender, e);
    }

    /// <summary>Accepts a number or a calculation; anything else keeps the window open with a message.</summary>
    private void AmountApply_Click(object sender, RoutedEventArgs e)
    {
        if (!BudzetDomowy.Core.Services.AmountExpression.TryEvaluate(AmountBox.Text, out var amount) || amount <= 0)
        {
            AmountError.Text = Res.Get("BulkAmountInvalid");
            AmountError.Visibility = Visibility.Visible;
            return;
        }
        Vm?.ChangeAmountOfSelected(Math.Round(amount, 2, MidpointRounding.AwayFromZero));
        AmountFlyout.Hide();
    }

    private void BulkApply_Click(object sender, RoutedEventArgs e)
    {
        BulkCombo.Commit();
        if (BulkCombo.SelectedValue is Guid id) Vm?.ChangeCategoryOfSelected(id);
        BulkFlyout.Hide();
    }
}
