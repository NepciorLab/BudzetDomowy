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
/// The window for a new income: only date, amount, currency, category and note. Enter walks through the fields and ends on the
/// confirm button; Ctrl+Enter confirms from anywhere, Esc closes.
/// </summary>
public partial class IncomeWindow : Window
{
    private readonly EntryViewModel _vm;

    public IncomeWindow(EntryViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.Saved += () => DialogResult = true;
        Loaded += (_, _) => DateBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryConfirm()
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            FindAncestor<ExpressionBox>(focused)?.Commit();
            FindAncestor<FilterComboBox>(focused)?.Commit();
        }
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
        if (!save && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true } open && open is not FilterComboBox) return;

        e.Handled = true;
        if (save)
        {
            TryConfirm();
            return;
        }

        if (FindAncestor<DatePicker>(source) is { } picker && DateTime.TryParse(picker.Text, out var typed)) picker.SelectedDate = typed;
        FindAncestor<ExpressionBox>(source)?.Commit();
        FindAncestor<FilterComboBox>(source)?.Commit();

        if (ReferenceEquals(FindAncestor<TextBox>(source), NoteBox))
        {
            Dispatcher.BeginInvoke(() => ConfirmButton.Focus(), DispatcherPriority.Input);        // the last field: on to "confirm"
            return;
        }
        if (Keyboard.FocusedElement is UIElement element) element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
