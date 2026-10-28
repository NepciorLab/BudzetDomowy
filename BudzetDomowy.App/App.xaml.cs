using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.Infrastructure.Updates;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace BudzetDomowy.App;

public partial class App : Application
{
    private BudgetCore? _core;
    private Mutex? _instanceLock;
    private IDisposable? _activation;
    private ServiceProvider? _services;
    private DateTime _lastErrorToast = DateTime.MinValue;
    private StartupSplash? _splash;

    private void CloseSplash()
    {
        _splash?.Close();
        _splash = null;
    }

    private void Stage(string key) => _splash?.SetStage(Res.Get(key));

    protected override void OnStartup(StartupEventArgs e)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppPaths.Initialize(e.Args);
        string? moved = null;
        try { moved = AppPaths.MigrateFromLegacy(); }                       // data of older versions: from %LocalAppData% to the folder of the program
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moved = "move failed: " + ex.Message; }
        SetTaskbarIdentity();
        SetPolishCulture();
        AmountBoxes.Register();
        DatePickerKeys.Register();
        AppLog.Info($"Starting (profile: {AppPaths.Profile ?? "default"}, data in '{AppPaths.SettingsDir}').");
        if (moved is not null) AppLog.Info("Data moved next to the program: " + moved);
        InstallErrorHandlers();

        // two copies on the same profile would share one database and one journal: refuse politely
        // after a restart the previous copy may still be closing: wait for it instead of refusing
        var restarted = e.Args.Contains(AppRestarter.RestartedFlag);
        _instanceLock = new Mutex(false, @"Local\BudzetDomowy-" + (AppPaths.Profile ?? "default"));
        bool firstInstance;
        try { firstInstance = _instanceLock.WaitOne(restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { firstInstance = true; }
        var eventName = SingleInstance.EventName(AppPaths.Profile);
        if (!firstInstance)
        {
            // no message: the copy that runs is asked to come to the front (only a copy that cannot be asked, e.g. of an older version, is reported)
            if (!SingleInstance.Request(eventName, TimeSpan.FromSeconds(3)))
                MessageBox.Show(Res.Get("AlreadyRunning"), Res.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceLock.Dispose();
            _instanceLock = null;
            Shutdown();
            return;
        }
        _activation = SingleInstance.Listen(eventName, () => Dispatcher.BeginInvoke(() => SingleInstance.BringToFront(MainWindow as MainWindow, Windows.OfType<Window>())));

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        UseConfiguredLocalFolder(settings);
        AppLog.Info($"Startup: settings and theme ready after {started.ElapsedMilliseconds} ms.");

        // the window with what is going on: at once, before the data is opened (the first start asks questions in a window of its own)
        if (!string.IsNullOrWhiteSpace(settings.DataFolder))
        {
            var version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "";
            _splash = StartupSplash.Show(settings.Theme, Res.Get("AppTitle"), version.Length > 0 ? "v" + version : "", Res.Get("SplashStarting"));
            AppLog.Info($"Startup: start window shown after {started.ElapsedMilliseconds} ms.");
        }

        if (!string.IsNullOrWhiteSpace(settings.DataFolder))
        {
            try
            {
                Stage("SplashDatabase");
                _core = BudgetCore.Create(AppPaths.LocalDir, settings.DataFolder, settings.DeviceName, log: AppLog.Instance);
            }
            catch (NewerDataException ex)
            {
                // the data was written by a newer version: offer the update before showing any error
                CloseSplash();
                AppLog.Error("The local data is from a newer version of the program.", ex);
                HandleNewerDataAtStartup(settings);
                return;
            }
            catch (Exception ex)
            {
                CloseSplash();
                AppLog.Error("Startup failed.", ex);
                MessageBox.Show(Res.Get("StartupFailed"), Res.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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

        AppLog.Info($"Startup: data engine ready after {started.ElapsedMilliseconds} ms.");
        if (settings.ResetLocalDatabase)
        {
            // the data folder was changed: forget what was built from the old one, it is rebuilt from the new journals
            _core.Database.ClearMaterializedData();
            settings = settings with { ResetLocalDatabase = false };
            settings.Save();
        }

        var store = new SettingsStore(settings);
        UiPreferences.Instance.Initialize(store);
        ShortcutMap.Instance.Initialize(store);

        var services = new ServiceCollection()
            .AddSingleton(_core)
            .AddSingleton(store)
            .AddSingleton<DataHub>()
            .AddSingleton<ToastService>()
            .AddSingleton<IEntryLauncher, WpfEntryLauncher>()
            .AddSingleton<IPaymentDialogs, WpfPaymentDialogs>()
            .AddSingleton<IPaymentReminderPrompt, WpfPaymentReminderPrompt>()
            .AddSingleton<PaymentReminderRunner>()
            .AddSingleton<PaymentsViewModel>()
            .AddSingleton<TransactionsViewModel>()
            .AddSingleton<CategoriesViewModel>()
            .AddSingleton<EventsViewModel>()
            .AddSingleton<DashboardViewModel>()
            .AddSingleton<BudgetsViewModel>()
            .AddSingleton<ReportsViewModel>()
            .AddSingleton<RecurringViewModel>()
            .AddSingleton<DiagnosticsViewModel>()
            .AddSingleton<IAppRestarter, AppRestarter>()
            .AddSingleton<IFileDialogs, WpfFileDialogs>()
            .AddSingleton<IMessageDialogs, WpfMessageDialogs>()
            .AddSingleton<BackupViewModel>()
            .AddSingleton<IRecurringPrompt, WpfRecurringPrompt>()
            .AddSingleton<IUpdatePrompt, WpfUpdatePrompt>()
            .AddSingleton(_ => new GitHubReleases(GitHubReleases.CreateClient()))
            .AddSingleton<UpdateService>()
            .AddSingleton<RecurringRunner>()
            .AddSingleton<ImportMappingStore>()
            .AddSingleton<ExportActions>()
            .AddSingleton<IImportRowEditor, WpfImportRowEditor>()
            .AddTransient<ImportViewModel>()
            .AddSingleton<Func<ImportViewModel>>(sp => () => sp.GetRequiredService<ImportViewModel>())
            .AddSingleton<SettingsViewModel>()
            .AddSingleton(sp => MainViewModel.CreateLazy(sp.GetRequiredService<BudgetCore>(), sp.GetRequiredService<IEntryLauncher>(), sp,
                sp.GetRequiredService<ToastService>(), sp.GetRequiredService<RecurringRunner>(), sp.GetRequiredService<SettingsStore>()))
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();

        _services = services;

        // newer data found by the synchronisation: the update is offered at once instead of errors
        var watcher = new NewerDataWatcher(services.GetRequiredService<UpdateService>(), work => Dispatcher.BeginInvoke(async () =>
        {
            try { await work(); }
            catch (Exception ex) { AppLog.Warn("Offering the update failed: " + ex.Message); }
        }));
        _core.Sync.StatusChanged += (_, status) => watcher.Observe(status);

        // only the screen that is shown first is made now; the others when they are opened (the log tells how long each took)
        var last = started.ElapsedMilliseconds;
        void Step(string what)
        {
            AppLog.Info($"Startup: {what} ready after {started.ElapsedMilliseconds} ms (+{started.ElapsedMilliseconds - last} ms).");
            last = started.ElapsedMilliseconds;
        }
        Stage("SplashScreens");
        services.GetRequiredService<MainViewModel>(); Step("main model and the first screen");
        Stage("SplashWindow");
        var main = services.GetRequiredService<MainWindow>();
        Step("main window");
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.ContentRendered += (_, _) =>
        {
            AppLog.Info($"Startup: window drawn after {started.ElapsedMilliseconds} ms.");
            CloseSplash();
        };
        main.Show();
        Step("show");
        watcher.Observe(_core.Sync.Status);
        BusyStatus.Instance.Attach(services.GetRequiredService<DataHub>());
        services.GetRequiredService<PaymentReminderRunner>().Start(TimeSpan.FromSeconds(4), TimeSpan.FromHours(1));
        services.GetRequiredService<BackupViewModel>().StartDaily(TimeSpan.FromSeconds(25), TimeSpan.FromHours(3));
        StartUpdateCheck(services);
    }

    /// <summary>A few seconds after the start (when switched on in Settings) the program looks for a newer version on GitHub.</summary>
    private static async void StartUpdateCheck(ServiceProvider services)
    {
        try
        {
            if (Environment.ProcessPath is { } exe) UpdateInstaller.CleanUp(exe);
            await Task.Delay(TimeSpan.FromSeconds(8));
            await services.GetRequiredService<UpdateService>().RunAutomaticAsync();
        }
        catch (Exception e)
        {
            AppLog.Warn("Update check failed: " + e.Message);   // never a reason to disturb the user
        }
    }

    /// <summary>The local database is from a newer version: look for the update first; an error only when there is none (or it was declined).</summary>
    private async void HandleNewerDataAtStartup(AppSettings settings)
    {
        try
        {
            var updates = new UpdateService(new SettingsStore(settings), new GitHubReleases(GitHubReleases.CreateClient()), new WpfUpdatePrompt(), new AppRestarter());
            if (await updates.OfferUpdateAsync(dataIsNewer: true)) return;   // the program restarts itself
        }
        catch (Exception ex)
        {
            AppLog.Warn("Looking for an update failed: " + ex.Message);
        }
        MessageBox.Show(Res.Get("StartupNewerData"), Res.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(1);
    }

    private static void UseConfiguredLocalFolder(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalDataFolder)) return;
        try
        {
            Directory.CreateDirectory(settings.LocalDataFolder);
            AppPaths.UseLocalFolder(settings.LocalDataFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // e.g. a removed drive: carry on with the default folder; the database is rebuilt from the journals anyway
            AppLog.Warn($"Local folder '{settings.LocalDataFolder}' is not usable: {e.Message}");
        }
    }

    // ----- no unhandled error ever reaches the user as a stack trace: it goes to the log, the user sees a short calm note -----

    private void InstallErrorHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled error on the UI thread.", e.Exception);
            ShowUnexpected();
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("Fatal unhandled error" + (e.IsTerminating ? " (the program is closing)." : "."), e.ExceptionObject as Exception);
            if (e.IsTerminating)
                MessageBox.Show(Res.Get("FatalError"), Res.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved background task error.", e.Exception);
            e.SetObserved();
        };
    }

    private void ShowUnexpected()
    {
        if (DateTime.Now - _lastErrorToast < TimeSpan.FromSeconds(10)) return;   // a burst of errors is one note
        _lastErrorToast = DateTime.Now;
        if (_services?.GetService<ToastService>() is { } toast) toast.Show(Res.Get("UnexpectedError"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose();
        _instanceLock?.ReleaseMutex();
        _instanceLock?.Dispose();
        _core?.Dispose();
        AppLog.Info("Exit.");
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
