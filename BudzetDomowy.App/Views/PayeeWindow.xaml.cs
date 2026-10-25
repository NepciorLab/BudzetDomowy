using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

/// <summary>Name, account number (checked, shown in groups) and an optional category of a payee. Enter goes on to the next field and then to "save".</summary>
public partial class PayeeWindow : Window
{
    private readonly PayeeEditViewModel _vm;

    public PayeeWindow(PayeeEditViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.Saved += () => DialogResult = true;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void AccountBox_LostFocus(object sender, RoutedEventArgs e) => _vm.FormatAccount();

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
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null && !save) return;
        if (!save && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true } open && open is not FilterComboBox) return;

        e.Handled = true;
        FindAncestor<FilterComboBox>(source)?.Commit();
        if (save || FindAncestor<FilterComboBox>(source) is not null)
        {
            _vm.SaveCommand.Execute(null);                  // the last field (or Ctrl+Enter): save
            return;
        }
        if (Keyboard.FocusedElement is UIElement element)
        {
            _vm.FormatAccount();
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
