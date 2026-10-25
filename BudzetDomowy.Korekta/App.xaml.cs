using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace BudzetDomowy.Korekta;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var pl = new CultureInfo("pl-PL");
        CultureInfo.DefaultThreadCurrentCulture = pl;
        CultureInfo.DefaultThreadCurrentUICulture = pl;
        Thread.CurrentThread.CurrentCulture = pl;
        Thread.CurrentThread.CurrentUICulture = pl;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("pl-PL")));

        var vm = new MainViewModel(new WpfUi());
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        _ = vm.StartAsync(e.Args.FirstOrDefault(a => !a.StartsWith('-')));
    }
}
