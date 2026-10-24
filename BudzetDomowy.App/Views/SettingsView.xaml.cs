using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SettingsView : UserControl
{
    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SetUpdateToken(TokenBox.Password);
        TokenBox.Clear();
    }

    private static ShortcutRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ShortcutRow;

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is not { } row || DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            row.IsEditing = false;
            return;
        }
        vm.ApplyShortcut(row, key, Keyboard.Modifiers);
    }

    private void ShortcutBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RowOf(sender) is { } row) row.IsEditing = false;
    }

    private void ShortcutBox_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is UIElement box) box.Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.ImportRequested -= OnImportRequested;
            if (e.NewValue is SettingsViewModel vm) vm.ImportRequested += OnImportRequested;
        };
    }

    private void OnImportRequested(ImportViewModel vm)
    {
        var window = new ImportWindow(vm) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is SettingsViewModel settings && e.NewValue is CategoryNode node && !ReferenceEquals(settings.Categories.SelectedNode, node))
            settings.Categories.SelectedNode = node;
    }
}
