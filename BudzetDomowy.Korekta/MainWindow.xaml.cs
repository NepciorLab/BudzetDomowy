using System.Windows;
using System.Windows.Controls;

namespace BudzetDomowy.Korekta;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(EntriesGrid.SelectedItems.OfType<TxRow>());

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        EntriesGrid.SelectAll();
        EntriesGrid.Focus();
    }
}
