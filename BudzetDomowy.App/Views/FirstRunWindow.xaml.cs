using BudzetDomowy.App.Infrastructure;
using System.Windows;
using BudzetDomowy.App.ViewModels;
using Microsoft.Win32;

namespace BudzetDomowy.App.Views;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;

    public FirstRunWindow(FirstRunViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        vm.CloseRequested += ok =>
        {
            DialogResult = ok;
            Close();
        };
        Loaded += (_, _) => FolderBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = BudzetDomowy.App.Infrastructure.Res.Get("WizardBrowseTitle"),
            InitialDirectory = Directory.Exists(_vm.FolderPath) ? _vm.FolderPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == true) _vm.FolderPath = dialog.FolderName;
    }
}
