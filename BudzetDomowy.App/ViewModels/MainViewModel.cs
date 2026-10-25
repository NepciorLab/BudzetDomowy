using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private readonly Dictionary<string, object> _pages;

    public BudgetCore Core { get; }
    public QuickAddViewModel QuickAdd { get; }
    public SettingsViewModel Settings { get; }
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

    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The window shows the details dialog.</summary>
    public event Action? SyncDetailsRequested;

    public MainViewModel(BudgetCore core, QuickAddViewModel quickAdd, TransactionsViewModel transactions,
        SettingsViewModel settings, EventsViewModel events, DashboardViewModel dashboard, BudgetsViewModel budgets,
        ReportsViewModel reports, RecurringViewModel recurring, ToastService toast, RecurringRunner runner, SettingsStore store)
    {
        Core = core;
        _runner = runner;
        _store = store;
        QuickAdd = quickAdd;
        Settings = settings;
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
            new("reports", Res.Get("NavReports"), "", ShortcutMap.Instance.DisplayOf("Go.reports")),
            new("settings", Res.Get("NavSettings"), "", ShortcutMap.Instance.DisplayOf("Go.settings")),
        ];

        _pages = new Dictionary<string, object>
        {
            ["dashboard"] = dashboard,
            ["transactions"] = transactions,
            ["budgets"] = budgets,
            ["events"] = events,
            ["recurring"] = recurring,
            ["reports"] = reports,
            ["settings"] = settings,
        };

        var profile = AppPaths.Profile is null ? "" : Res.Format("ProfileSuffix", AppPaths.Profile);
        WindowTitle = Res.Get("AppTitle") + profile;
        DeviceLabel = core.Device.DeviceName + profile;

        UpdateToday();
        _clock.Tick += (_, _) => UpdateToday();
        _clock.Start();

        ShortcutMap.Instance.Changed += _ =>
        {
            foreach (var item in NavItems) item.Shortcut = ShortcutMap.Instance.DisplayOf(ShortcutMap.GoPrefix + item.Key);
        };
        SelectedNav = NavItems.FirstOrDefault(n => n.Key == StartPageKey(store.Current)) ?? NavItems[0];
        core.Sync.StatusChanged += (_, s) => Application.Current.Dispatcher.BeginInvoke(() => ShowStatus(s));
        ShowStatus(core.Sync.Status);
        core.Sync.Start();

        // recurring entries that fell due while the program was closed (and again every few hours if it stays open)
        _recurringTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _recurringTimer.Tick += async (_, _) => await GenerateRecurringAsync();
        _recurringTimer.Start();
        _ = GenerateRecurringAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer _recurringTimer;
    private readonly RecurringRunner _runner;
    private readonly SettingsStore _store;

    private async Task GenerateRecurringAsync()
    {
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

    /// <summary>The page to open at startup: the chosen one, or the one used last.</summary>
    public static string StartPageKey(AppSettings s) => s.StartPage == "last" ? s.LastPage : s.StartPage;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        CurrentPage = _pages[value.Key];
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
