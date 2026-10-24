using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
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

    public CategoriesViewModel Categories { get; }

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

    public SettingsViewModel(BudgetCore core, SettingsStore store, CategoriesViewModel categories)
    {
        _core = core;
        _store = store;
        Categories = categories;
        _selectedTheme = Themes.First(t => t.Choice == store.Current.Theme);
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        ThemeService.Apply(value.Choice);
        _store.Update(s => s with { Theme = value.Choice });
    }

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
