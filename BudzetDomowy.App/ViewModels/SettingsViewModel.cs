using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record ThemeOption(ThemeChoice Choice, string Label)
{
    public override string ToString() => Label;
}

public sealed record ShortcutItem(string Keys, string Description);

public sealed partial class SettingsViewModel : ObservableObject
{
    public const int ShortcutsTab = 5;

    private readonly BudgetCore _core;
    private readonly SettingsStore _store;
    private readonly Func<ImportViewModel> _createImport;
    private readonly ExportActions _exports;
    private readonly IBackgroundHost _background;
    private bool _restoringBackground;

    public CategoriesViewModel Categories { get; }
    public RecurringViewModel Recurring { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _backgroundMode;
    [ObservableProperty] private string _backgroundMessage = "";

    public IReadOnlyList<ShortcutItem> Shortcuts { get; } =
    [
        new("Enter", Res.Get("KeyEnter")),
        new("Ctrl + Enter", Res.Get("KeyCtrlEnter")),
        new("Tab / Shift + Tab", Res.Get("KeyTab")),
        new("Esc", Res.Get("KeyEsc")),
        new("F2", Res.Get("KeyF2")),
        new("F3", Res.Get("KeyF3")),
        new("F4", Res.Get("KeyF4")),
        new("Ctrl + L", Res.Get("KeyCtrlL")),
        new("Ctrl + Z", Res.Get("KeyCtrlZ")),
        new("Ctrl + 1 … Ctrl + 6", Res.Get("KeyCtrlDigits")),
        new("Del", Res.Get("KeyDel")),
        new("F1", Res.Get("KeyF1")),
        new("Ctrl + Alt + B", Res.Get("KeyGlobal")),
    ];

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeChoice.System, Res.Get("ThemeSystem")),
        new(ThemeChoice.Light, Res.Get("ThemeLight")),
        new(ThemeChoice.Dark, Res.Get("ThemeDark")),
    ];

    [ObservableProperty] private ThemeOption _selectedTheme;

    public string DataFolder => _core.DataFolder;
    public string LocalFolder => AppPaths.LocalDir;
    public string DeviceName => _core.Device.DeviceName;
    public string DeviceId => _core.Device.DeviceId;

    public SettingsViewModel(BudgetCore core, SettingsStore store, CategoriesViewModel categories, RecurringViewModel recurring,
        Func<ImportViewModel> createImport, ExportActions exports, DiagnosticsViewModel diagnostics, IBackgroundHost background)
    {
        _core = core;
        _store = store;
        Categories = categories;
        Recurring = recurring;
        _createImport = createImport;
        _exports = exports;
        _background = background;
        Diagnostics = diagnostics;
        _backgroundMode = store.Current.BackgroundMode && background.IsActive;
        _selectedTheme = Themes.First(t => t.Choice == store.Current.Theme);
    }

    partial void OnBackgroundModeChanged(bool value)
    {
        if (_restoringBackground) return;
        BackgroundMessage = "";
        if (value)
        {
            if (!_background.Start(out var error))
            {
                BackgroundMessage = error ?? "";
                _restoringBackground = true;
                BackgroundMode = false;
                _restoringBackground = false;
                return;
            }
        }
        else
        {
            _background.Stop();
        }
        _store.Update(s => s with { BackgroundMode = value });
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        ThemeService.Apply(value.Choice);
        _store.Update(s => s with { Theme = value.Choice });
    }

    /// <summary>The view opens the import wizard window for the view model it is given.</summary>
    public event Action<ImportViewModel>? ImportRequested;

    [RelayCommand]
    private void OpenImport() => ImportRequested?.Invoke(_createImport());

    [RelayCommand]
    private void ExportAllXlsx() => _exports.Transactions(_core.Reader.GetTransactions(), csv: false);

    [RelayCommand]
    private void ExportAllCsv() => _exports.Transactions(_core.Reader.GetTransactions(), csv: true);

    [RelayCommand]
    private void ExportThisYear() => _exports.Report(ReportScope.Year, DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolder);

    [RelayCommand]
    private void OpenLocalFolder() => Open(LocalFolder);

    private static void Open(string path)
    {
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
