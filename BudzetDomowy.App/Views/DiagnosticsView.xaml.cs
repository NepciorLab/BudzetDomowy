using System.Windows.Controls;

namespace BudzetDomowy.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is ViewModels.DiagnosticsViewModel vm) vm.Refresh();
        };
    }
}
