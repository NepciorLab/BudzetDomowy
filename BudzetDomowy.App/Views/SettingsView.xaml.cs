using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SettingsView : UserControl
{
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

    private PlaceHintsViewModel? Places => PlacesGrid.DataContext as PlaceHintsViewModel;

    private void PlacesRemoveSelected_Click(object sender, RoutedEventArgs e) =>
        Places?.RemoveMany(PlacesGrid.SelectedItems.OfType<PlaceHintRow>().ToList());

    /// <summary>Delete removes the chosen places, Home and End go to the top and the bottom of the list (Shift extends the choice); typing a weight is left alone.</summary>
    private void PlacesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || PlacesGrid.Items.Count == 0) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var other = (Keyboard.Modifiers & ~ModifierKeys.Shift & ~ModifierKeys.Control) != 0;
        if (other) return;

        switch (e.Key)
        {
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                Places?.RemoveMany(PlacesGrid.SelectedItems.OfType<PlaceHintRow>().ToList());
                e.Handled = true;
                break;
            case Key.Home or Key.End:
                var last = e.Key == Key.End ? PlacesGrid.Items.Count - 1 : 0;
                var anchor = PlacesGrid.SelectedItem is { } current ? PlacesGrid.Items.IndexOf(current) : last;
                if (shift)
                {
                    PlacesGrid.SelectedItems.Clear();
                    for (var i = Math.Min(anchor, last); i <= Math.Max(anchor, last); i++) PlacesGrid.SelectedItems.Add(PlacesGrid.Items[i]);
                }
                else
                {
                    PlacesGrid.SelectedItems.Clear();
                    PlacesGrid.SelectedIndex = last;
                }
                PlacesGrid.ScrollIntoView(PlacesGrid.Items[last]);
                e.Handled = true;
                break;
        }
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
