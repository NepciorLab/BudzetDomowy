using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SettingsView : UserControl
{
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
