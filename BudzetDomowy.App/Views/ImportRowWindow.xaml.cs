using System.Windows;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;

namespace BudzetDomowy.App.Views;

/// <summary>Correcting one row of the import preview.</summary>
public partial class ImportRowWindow : Window
{
    public ImportRowWindow(ImportRowEditViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.CloseRequested += ok => DialogResult = ok;
        Loaded += (_, _) => (vm.Date is null ? (UIElement)DateBox : AmountBox).Focus();
    }
}

public sealed class WpfImportRowEditor : IImportRowEditor
{
    public ImportRowFix? Edit(ImportRow row, IReadOnlyList<CategoryOption> categories, string problem)
    {
        var vm = new ImportRowEditViewModel(row, categories, problem);
        var window = new ImportRowWindow(vm);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is not null && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true ? vm.Result : null;
    }
}
