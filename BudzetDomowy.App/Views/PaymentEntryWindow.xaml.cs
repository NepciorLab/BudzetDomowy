using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

/// <summary>
/// Typing the bills of one payee. Enter walks date, title, amount; Enter in the amount accepts it and opens the next row, until an
/// empty row is left: then Enter goes to the confirm button. Ctrl+Enter confirms from anywhere, Esc closes.
/// </summary>
public partial class PaymentEntryWindow : Window
{
    private readonly PaymentEntryViewModel _vm;

    public PaymentEntryWindow(PaymentEntryViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.Saved += () => DialogResult = true;
        Loaded += (_, _) => (vm.SelectedPayee is null ? (UIElement)PayeeBox : FirstDate()).Focus();
    }

    private UIElement FirstDate() => InRow<DatePicker>(_vm.Rows[0]) ?? (UIElement)PayeeBox;

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryConfirm()
    {
        if (Keyboard.FocusedElement is DependencyObject focused) FindAncestor<ExpressionBox>(focused)?.Commit();
        _vm.Confirm();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (e.OriginalSource is DependencyObject origin && FindAncestor<ComboBox>(origin) is { IsDropDownOpen: true }) return;
            e.Handled = true;
            DialogResult = false;
            return;
        }
        if (e.Key != Key.Enter || e.OriginalSource is not DependencyObject source) return;

        var save = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null && !save) return;      // Enter on a button presses it
        if (!save && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true }) return;

        e.Handled = true;
        if (save)
        {
            TryConfirm();
            return;
        }

        if (FindAncestor<DatePicker>(source) is { } picker && DateTime.TryParse(picker.Text, out var typed)) picker.SelectedDate = typed;

        if (FindAncestor<ExpressionBox>(source) is { } box && box.DataContext is PaymentRowViewModel row)
        {
            box.Commit();
            Go(_vm.CommitRow(row));
            return;
        }
        if (Keyboard.FocusedElement is UIElement element) element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void Go(PaymentEntryFocus target)
    {
        switch (target.Target)
        {
            case PaymentFocus.Amount:
                Dispatcher.BeginInvoke(() => InRow<ExpressionBox>(target.Row!)?.FocusAndSelect(), DispatcherPriority.Input);
                break;
            case PaymentFocus.Date:
                Dispatcher.BeginInvoke(() => InRow<DatePicker>(target.Row!)?.Focus(), DispatcherPriority.Input);
                break;
            default:
                Dispatcher.BeginInvoke(() => ConfirmButton.Focus(), DispatcherPriority.Input);
                break;
        }
    }

    private T? InRow<T>(PaymentRowViewModel row) where T : DependencyObject
    {
        RowsList.UpdateLayout();
        return RowsList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container ? Descendants<T>(container).FirstOrDefault() : null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
