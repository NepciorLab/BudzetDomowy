using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class QuickAddBar : UserControl
{
    private QuickAddViewModel? _subscribed;

    public QuickAddBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.Saved -= OnSaved;
            _subscribed.AmountRejected -= FocusAmount;
        }
        _subscribed = DataContext as QuickAddViewModel;
        if (_subscribed is not null)
        {
            _subscribed.Saved += OnSaved;
            _subscribed.AmountRejected += FocusAmount;
        }
    }

    private void OnSaved()
    {
        CategoryBox.Reset();
        FocusAmount();
    }

    public void FocusAmount()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AmountBox.Focus();
            AmountBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void FocusCurrency() => CurrencyBox.Focus();

    public void FocusRate()
    {
        if (RateBox.IsVisible)
        {
            RateBox.Focus();
            RateBox.SelectAll();
        }
    }

    /// <summary>
    /// Enter moves to the next field (like Tab) after resolving what was typed; only Enter on the Add button saves.
    /// Ctrl+Enter saves from any field.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _subscribed is null) return;
        var save = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.OriginalSource is DependencyObject source)
        {
            if (FindAncestor<ButtonBase>(source) is not null) return; // Enter on a button presses it
            if (!save && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true } open && open is not FilterComboBox) return;

            FindAncestor<FilterComboBox>(source)?.Commit();
            if (FindAncestor<DatePicker>(source) is { } picker && DateTime.TryParse(picker.Text, out var typed))
                picker.SelectedDate = typed;
        }

        e.Handled = true;
        if (save) _subscribed.AddCommand.Execute(null);
        else (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
