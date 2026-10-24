using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is SettingsViewModel settings && e.NewValue is CategoryNode node && !ReferenceEquals(settings.Categories.SelectedNode, node))
            settings.Categories.SelectedNode = node;
    }
}
