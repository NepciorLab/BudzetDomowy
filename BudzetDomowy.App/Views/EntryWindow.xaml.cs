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
/// The window for a new expense or income. Enter walks through the date, the place and the total and then goes straight to the first
/// line (the currency, the event and the note are usually the defaults: Tab or the mouse reach them). In the lines: category, then
/// amount (selected, so Enter accepts it); an amount less than what is left opens the next line, and when nothing is left Enter goes
/// to the confirm button. Ctrl+Enter confirms from anywhere, Esc closes.
/// </summary>
public partial class EntryWindow : Window
{
    private readonly EntryViewModel _vm;

    public EntryWindow(EntryViewModel vm)
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
        CommitFocused();
        if (!_vm.Confirm()) return;
    }

    /// <summary>What was typed in the focused box is accepted before anything is checked.</summary>
    private void CommitFocused()
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            FindAncestor<ExpressionBox>(focused)?.Commit();
            FindAncestor<FilterComboBox>(focused)?.Commit();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (e.OriginalSource is DependencyObject origin && FindAncestor<ComboBox>(origin) is { IsDropDownOpen: true }) return;   // the list closes first
            if (e.OriginalSource is DependencyObject place && FindAncestor<PlaceBox>(place) is { IsSuggesting: true }) return;
            e.Handled = true;
            DialogResult = false;
            return;
        }
        if (e.Key != Key.Enter) return;

        var save = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null && !save) return;      // Enter on a button presses it
        if (FindAncestor<PlaceBox>(source) is { } placeBox) placeBox.Accept();                                        // Enter takes the completion shown in the place box
        if (!save && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true } open && open is not FilterComboBox) return;

        e.Handled = true;
        if (save)
        {
            TryConfirm();
            return;
        }

        if (FindAncestor<DatePicker>(source) is { } picker && DateTime.TryParse(picker.Text, out var typed)) picker.SelectedDate = typed;

        if (FindAncestor<ExpressionBox>(source) is { } box)
        {
            box.Commit();
            if (box.Name == "LineAmount" && box.DataContext is EntryLineViewModel line)
            {
                Go(_vm.CommitLine(line));
                return;
            }
            if (box.Name == "TotalBox")
            {
                _vm.Recalculate();
                if (_vm.Lines.Count > 0)
                {
                    FocusCategory(_vm.Lines[0]);               // past the currency, the event and the note
                    return;
                }
            }
        }
        else if (FindAncestor<FilterComboBox>(source) is { } combo)
        {
            combo.Commit();
            if (combo.Name == "LineCategory" && combo.DataContext is EntryLineViewModel row)
            {
                FocusAmount(row);                              // category accepted: on to its amount, selected
                return;
            }
        }

        if (Keyboard.FocusedElement is UIElement element) element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void Go(EntryFocus target)
    {
        switch (target.Target)
        {
            case EntryFocusTarget.Total:
                TotalBox.FocusAndSelect();
                break;
            case EntryFocusTarget.LineAmount:
                FocusAmount(target.Line!);
                break;
            case EntryFocusTarget.LineCategory:
                FocusCategory(target.Line!);
                break;
            default:
                Dispatcher.BeginInvoke(() => ConfirmButton.Focus(), DispatcherPriority.Input);
                break;
        }
    }

    private T? InLine<T>(EntryLineViewModel line) where T : DependencyObject
    {
        LinesList.UpdateLayout();
        return LinesList.ItemContainerGenerator.ContainerFromItem(line) is DependencyObject container ? Descendants<T>(container).FirstOrDefault() : null;
    }

    private void FocusCategory(EntryLineViewModel line) => Dispatcher.BeginInvoke(() =>
    {
        if (InLine<FilterComboBox>(line) is { } combo) combo.Focus();
    }, DispatcherPriority.Input);

    private void FocusAmount(EntryLineViewModel line) => Dispatcher.BeginInvoke(() =>
    {
        if (InLine<ExpressionBox>(line) is { } box) box.FocusAndSelect();
    }, DispatcherPriority.Input);

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
