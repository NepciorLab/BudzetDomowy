using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core;
using Microsoft.Extensions.DependencyInjection;

namespace BudzetDomowy.App;

public partial class App : Application
{
    private BudgetCore? _core;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppPaths.Initialize(e.Args);
        SetTaskbarIdentity();
        SetPolishCulture();

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);

        if (!string.IsNullOrWhiteSpace(settings.DataFolder))
        {
            try
            {
                _core = BudgetCore.Create(AppPaths.LocalDir, settings.DataFolder, settings.DeviceName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Res.Format("StartupFailed", ex.Message), Res.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }
        else
        {
            var wizard = new FirstRunViewModel(settings);
            var window = new FirstRunWindow(wizard);
            if (window.ShowDialog() != true || wizard.Result is null)
            {
                Shutdown();
                return;
            }
            _core = wizard.Result;
            settings = wizard.ResultSettings!;
        }

        var services = new ServiceCollection()
            .AddSingleton(_core)
            .AddSingleton(new SettingsStore(settings))
            .AddSingleton<DataHub>()
            .AddSingleton<ToastService>()
            .AddSingleton<QuickAddViewModel>()
            .AddSingleton<TransactionsViewModel>()
            .AddSingleton<CategoriesViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();

        var main = services.GetRequiredService<MainWindow>();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _core?.Dispose();
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Own taskbar identity, so the taskbar button shows this app's icon and each profile gets its own button.</summary>
    private static void SetTaskbarIdentity()
    {
        try { SetCurrentProcessExplicitAppUserModelID("BudzetDomowy.App" + (AppPaths.Profile is null ? "" : "." + AppPaths.Profile)); }
        catch (Exception) { /* cosmetic only */ }
    }

    private static void SetPolishCulture()
    {
        var pl = new CultureInfo("pl-PL");
        CultureInfo.DefaultThreadCurrentCulture = pl;
        CultureInfo.DefaultThreadCurrentUICulture = pl;
        CultureInfo.CurrentCulture = pl;
        CultureInfo.CurrentUICulture = pl;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(pl.IetfLanguageTag)));
    }
}

/// <summary>Holds the current local settings and writes them on every change.</summary>
public sealed class SettingsStore
{
    public AppSettings Current { get; private set; }

    public SettingsStore(AppSettings initial) => Current = initial;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        Current.Save();
    }
}
