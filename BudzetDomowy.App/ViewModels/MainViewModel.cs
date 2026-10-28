using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace BudzetDomowy.App.ViewModels;

public sealed partial class NavItem(string key, string title, string glyph, string shortcut) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    [ObservableProperty] private string _shortcut = shortcut;
}

public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title) => Title = title;
    public string Title { get; }
    public string Message => Res.Get("PlaceholderMessage");
}

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The screens, each made when it is shown for the first time: making all of them at the start took most of the time before the window appeared.</summary>
    private readonly Dictionary<string, Lazy<object>> _pages;
    private readonly Lazy<SettingsViewModel> _settings;

    public BudgetCore Core { get; }
    public SettingsViewModel Settings => _settings.Value;
    public string VersionLabel { get; }
    public ToastService Toast { get; }
    public IReadOnlyList<NavItem> NavItems { get; }
    public string WindowTitle { get; }
    public string DeviceLabel { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private Brush _syncBrush = Brushes.Gray;
    [ObservableProperty] private string _syncGlyph = "●";
    [ObservableProperty] private string _todayText = "";
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _expenseHint = "";
    [ObservableProperty] private string _incomeHint = "";

    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The window shows the details dialog.</summary>
    public event Action? SyncDetailsRequested;

    /// <summary>With all the screens made already (tests).</summary>
    public MainViewModel(BudgetCore core, IEntryLauncher launcher, TransactionsViewModel transactions,
        SettingsViewModel settings, EventsViewModel events, DashboardViewModel dashboard, BudgetsViewModel budgets,
        ReportsViewModel reports, RecurringViewModel recurring, PaymentsViewModel payments, ToastService toast, RecurringRunner runner, SettingsStore store)
        : this(core, launcher, new Dictionary<string, Func<object>>
        {
            ["dashboard"] = () => dashboard,
            ["transactions"] = () => transactions,
            ["budgets"] = () => budgets,
            ["events"] = () => events,
            ["recurring"] = () => recurring,
            ["payments"] = () => payments,
            ["reports"] = () => reports,
            ["settings"] = () => settings,
        }, toast, runner, store)
    {
    }

    /// <summary>The program: every screen is taken from <paramref name="services"/> when it is shown first.</summary>
    public static MainViewModel CreateLazy(BudgetCore core, IEntryLauncher launcher, IServiceProvider services, ToastService toast, RecurringRunner runner, SettingsStore store) =>
        new(core, launcher, new Dictionary<string, Func<object>>
        {
            ["dashboard"] = () => services.GetRequiredService<DashboardViewModel>(),
            ["transactions"] = () => services.GetRequiredService<TransactionsViewModel>(),
            ["budgets"] = () => services.GetRequiredService<BudgetsViewModel>(),
            ["events"] = () => services.GetRequiredService<EventsViewModel>(),
            ["recurring"] = () => services.GetRequiredService<RecurringViewModel>(),
            ["payments"] = () => services.GetRequiredService<PaymentsViewModel>(),
            ["reports"] = () => services.GetRequiredService<ReportsViewModel>(),
            ["settings"] = () => services.GetRequiredService<SettingsViewModel>(),
        }, toast, runner, store);

    private MainViewModel(BudgetCore core, IEntryLauncher launcher, IReadOnlyDictionary<string, Func<object>> pages, ToastService toast, RecurringRunner runner, SettingsStore store)
    {
        Core = core;
        _runner = runner;
        _store = store;
        _launcher = launcher;
        VersionLabel = "v" + (typeof(MainViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "?");
        Toast = toast;

        NavItems =
        [
            new("dashboard", Res.Get("NavDashboard"), "", ShortcutMap.Instance.DisplayOf("Go.dashboard")),
            new("transactions", Res.Get("NavTransactions"), "", ShortcutMap.Instance.DisplayOf("Go.transactions")),
            new("budgets", Res.Get("NavBudgets"), "", ShortcutMap.Instance.DisplayOf("Go.budgets")),
            new("events", Res.Get("NavEvents"), "", ShortcutMap.Instance.DisplayOf("Go.events")),
            new("recurring", Res.Get("NavRecurring"), "", ShortcutMap.Instance.DisplayOf("Go.recurring")),
            new("payments", Res.Get("NavPayments"), "", ShortcutMap.Instance.DisplayOf("Go.payments")),
            new("reports", Res.Get("NavReports"), "", ShortcutMap.Instance.DisplayOf("Go.reports")),
            new("settings", Res.Get("NavSettings"), "", ShortcutMap.Instance.DisplayOf("Go.settings")),
        ];

        _pages = pages.ToDictionary(kv => kv.Key, kv => new Lazy<object>(() =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var page = kv.Value();
            AppLog.Info($"Screen '{kv.Key}' made in {timer.ElapsedMilliseconds} ms.");
            return page;
        }));
        _settings = new Lazy<SettingsViewModel>(() => (SettingsViewModel)_pages["settings"].Value);

        var profile = AppPaths.Profile is null ? "" : Res.Format("ProfileSuffix", AppPaths.Profile);
        WindowTitle = Res.Get("AppTitle") + profile;
        DeviceLabel = core.Device.DeviceName + profile;

        UpdateToday();
        _clock.Tick += (_, _) => UpdateToday();
        _clock.Start();

        UpdateEntryHints();
        ShortcutMap.Instance.Changed += _ =>
        {
            foreach (var item in NavItems) item.Shortcut = ShortcutMap.Instance.DisplayOf(ShortcutMap.GoPrefix + item.Key);
            UpdateEntryHints();
        };
        SelectedNav = NavItems.FirstOrDefault(n => n.Key == StartPageKey(store.Current)) ?? NavItems[0];
        core.Sync.StatusChanged += (_, s) => Application.Current.Dispatcher.BeginInvoke(() => ShowStatus(s));
        ShowStatus(core.Sync.Status);
        core.Sync.Start();

        // Recurring entries that fell due while the program was closed: checked shortly after the start, when the window is already
        // on screen (the questions can only be asked in it), then every quarter of an hour and whenever the window is activated,
        // so a program left open over midnight asks the next day too.
        _recurringTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _recurringTimer.Tick += async (_, _) =>
        {
            _recurringTimer.Interval = TimeSpan.FromMinutes(15);
            await GenerateRecurringAsync();
        };
        _recurringTimer.Start();
    }

    private DateTime _lastRecurringCheck = DateTime.MinValue;

    /// <summary>For tests: the next activation checks at once, whatever the last check was.</summary>
    public void ForgetLastRecurringCheck() => _lastRecurringCheck = DateTime.MinValue;

    /// <summary>Checks the recurring entries again when the program window comes back to the front (at most once a minute).</summary>
    public async Task CheckRecurringOnActivationAsync()
    {
        if (DateTime.Now - _lastRecurringCheck < TimeSpan.FromMinutes(1)) return;
        await GenerateRecurringAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer _recurringTimer;
    private readonly RecurringRunner _runner;
    private readonly SettingsStore _store;
    private readonly IEntryLauncher _launcher;

    private async Task GenerateRecurringAsync()
    {
        _lastRecurringCheck = DateTime.Now;
        try
        {
            var run = await _runner.RunAsync(DateOnly.FromDateTime(DateTime.Today));
            if (run.Created > 0) Toast.Show(Res.Format("RecurGeneratedToast", run.Created));
        }
        catch (UpdateRequiredException) { /* shown by the sync indicator */ }
        catch (Exception) { /* a failed background run is retried at the next tick */ }
    }

    /// <summary>Weekday, day, month and year in capitals, e.g. SOBOTA, 24 PAŹDZIERNIKA 2026.</summary>
    private void UpdateToday()
    {
        var now = DateTime.Now;
        TodayText = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
        ClockText = now.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private void UpdateEntryHints()
    {
        ExpenseHint = Res.Format("EntryHintExpense", ShortcutMap.Instance.DisplayOf(ShortcutMap.NewExpense));
        IncomeHint = Res.Format("EntryHintIncome", ShortcutMap.Instance.DisplayOf(ShortcutMap.NewIncome));
    }

    /// <summary>"W": the window for a new expense (also from the button at the top).</summary>
    [RelayCommand]
    private void NewExpense() => _launcher.Open(income: false);

    [RelayCommand]
    private void NewIncome() => _launcher.Open(income: true);

    /// <summary>The calculator button next to the entry buttons.</summary>
    [RelayCommand]
    private void OpenCalculator() => _launcher.OpenCalculator();

    /// <summary>The page to open at startup: the chosen one, or the one used last.</summary>
    public static string StartPageKey(AppSettings s) => s.StartPage == "last" ? s.LastPage : s.StartPage;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        var existed = _pages[value.Key].IsValueCreated;                  // a screen made just now has just read everything
        CurrentPage = _pages[value.Key].Value;
        if (existed && CurrentPage is IRefreshOnShow screen) screen.RefreshOnShow();
        if (_store.Current.LastPage != value.Key) _store.Update(s => s with { LastPage = value.Key });
    }

    public void Navigate(string key) => SelectedNav = NavItems.First(n => n.Key == key);

    [RelayCommand]
    private void ShowSyncDetails() => SyncDetailsRequested?.Invoke();

    [RelayCommand]
    private void Undo() => Toast.TryUndoLast();

    /// <summary>Alt + arrows: the screen on show moves to the previous or next week, month or year.</summary>
    [RelayCommand]
    private void PreviousPeriod() => (CurrentPage as IPeriodNavigable)?.PreviousPeriod();

    [RelayCommand]
    private void NextPeriod() => (CurrentPage as IPeriodNavigable)?.NextPeriod();

    private void ShowStatus(SyncStatus s)
    {
        switch (s.State)
        {
            case SyncState.Syncing:
                SyncText = Res.Get("SyncRunning");
                SyncGlyph = "↻";
                SyncBrush = FindBrush("SystemControlForegroundAccentBrush", Brushes.SteelBlue);
                break;
            case SyncState.Synced:
                SyncText = Res.Format("SyncDone", (s.LastSyncUtc ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
                SyncGlyph = "●";
                SyncBrush = (Brush)Application.Current.Resources["IncomeBrush"];
                break;
            case SyncState.Problem:
                SyncText = s.UpdateRequired ? Res.Get("SyncUpdateRequired") : Res.Format("SyncProblem", s.Issues.Count);
                SyncGlyph = "▲";
                SyncBrush = (Brush)Application.Current.Resources[s.UpdateRequired ? "ExpenseBrush" : "WarningBrush"];
                break;
            default:
                SyncText = Res.Get("SyncWaiting");
                SyncGlyph = "●";
                SyncBrush = Brushes.Gray;
                break;
        }
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;
}
