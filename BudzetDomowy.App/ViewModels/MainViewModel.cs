using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record NavItem(string Key, string Title, string Glyph, string Shortcut);

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
    public ToastService Toast { get; }
    public IReadOnlyList<NavItem> NavItems { get; }
    public string WindowTitle { get; }
    public string DeviceLabel { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private Brush _syncBrush = Brushes.Gray;
    [ObservableProperty] private string _syncGlyph = "●";

    /// <summary>The window shows the details dialog.</summary>
    public event Action? SyncDetailsRequested;

    public MainViewModel(BudgetCore core, QuickAddViewModel quickAdd, TransactionsViewModel transactions,
        SettingsViewModel settings, ToastService toast)
    {
        Core = core;
        QuickAdd = quickAdd;
        Toast = toast;

        NavItems =
        [
            new("dashboard", Res.Get("NavDashboard"), "", "Ctrl+1"),
            new("transactions", Res.Get("NavTransactions"), "", "Ctrl+2"),
            new("budgets", Res.Get("NavBudgets"), "", "Ctrl+3"),
            new("events", Res.Get("NavEvents"), "", "Ctrl+4"),
            new("reports", Res.Get("NavReports"), "", "Ctrl+5"),
            new("settings", Res.Get("NavSettings"), "", "Ctrl+6"),
        ];

        _pages = new Dictionary<string, object>
        {
            ["dashboard"] = new PlaceholderViewModel(Res.Get("NavDashboard")),
            ["transactions"] = transactions,
            ["budgets"] = new PlaceholderViewModel(Res.Get("NavBudgets")),
            ["events"] = new PlaceholderViewModel(Res.Get("NavEvents")),
            ["reports"] = new PlaceholderViewModel(Res.Get("NavReports")),
            ["settings"] = settings,
        };

        var profile = AppPaths.Profile is null ? "" : Res.Format("ProfileSuffix", AppPaths.Profile);
        WindowTitle = Res.Get("AppTitle") + profile;
        DeviceLabel = core.Device.DeviceName + profile;

        SelectedNav = NavItems[1];
        core.Sync.StatusChanged += (_, s) => Application.Current.Dispatcher.BeginInvoke(() => ShowStatus(s));
        ShowStatus(core.Sync.Status);
        core.Sync.Start();
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null) CurrentPage = _pages[value.Key];
    }

    public void Navigate(string key) => SelectedNav = NavItems.First(n => n.Key == key);

    [RelayCommand]
    private void ShowSyncDetails() => SyncDetailsRequested?.Invoke();

    [RelayCommand]
    private void Undo() => Toast.TryUndoLast();

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
