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

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly SettingsStore _store;
    private readonly Func<ImportViewModel> _createImport;
    private readonly ExportActions _exports;

    public CategoriesViewModel Categories { get; }
    public RecurringViewModel Recurring { get; }

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
        Func<ImportViewModel> createImport, ExportActions exports)
    {
        _core = core;
        _store = store;
        Categories = categories;
        Recurring = recurring;
        _createImport = createImport;
        _exports = exports;
        _selectedTheme = Themes.First(t => t.Choice == store.Current.Theme);
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
