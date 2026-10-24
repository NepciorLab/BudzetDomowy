using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using System.Collections.ObjectModel;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Sync;
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
    public const int ShortcutsTab = 4;

    private readonly BudgetCore _core;
    private readonly SettingsStore _store;
    private readonly Func<ImportViewModel> _createImport;
    private readonly ExportActions _exports;
    private readonly IBackgroundHost _background;
    private readonly IFileDialogs _dialogs;
    private readonly IAppRestarter _restarter;
    private FolderCheck? _pendingCheck;
    private bool _restoringBackground;

    public CategoriesViewModel Categories { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty] private int _selectedTab;

    /// <summary>Display preferences (tables sized to their content).</summary>
    public UiPreferences Preferences => UiPreferences.Instance;

    // ----- changing the folders -----
    public ObservableCollection<CheckLine> PendingChecks { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingDataFolder))] private string _pendingDataFolder = "";
    [ObservableProperty] private bool _copyJournals;
    [ObservableProperty] private bool _canApplyDataFolder;
    [ObservableProperty] private string _folderMessage = "";
    [ObservableProperty] private bool _restartNeeded;

    public bool HasPendingDataFolder => PendingDataFolder.Length > 0;
    [ObservableProperty] private bool _backgroundMode;
    [ObservableProperty] private string _backgroundMessage = "";

    public IReadOnlyList<ShortcutItem> Shortcuts { get; } =
    [
        new("Enter", Res.Get("KeyEnter")),
        new("Ctrl + Enter", Res.Get("KeyCtrlEnter")),
        new("Tab / Shift + Tab", Res.Get("KeyTab")),
        new("Esc", Res.Get("KeyEsc")),
        new("Esc (w tabeli)", Res.Get("KeyEscTable")),
        new("F2", Res.Get("KeyF2")),
        new("F3", Res.Get("KeyF3")),
        new("F4", Res.Get("KeyF4")),
        new("Ctrl + L", Res.Get("KeyCtrlL")),
        new("Ctrl + Z", Res.Get("KeyCtrlZ")),
        new("Ctrl + 1 … Ctrl + 7", Res.Get("KeyCtrlDigits")),
        new("Alt + ← / →", Res.Get("KeyAltArrows")),
        new("Ctrl + K", Res.Get("KeyCtrlK")),
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

    public SettingsViewModel(BudgetCore core, SettingsStore store, CategoriesViewModel categories,
        Func<ImportViewModel> createImport, ExportActions exports, DiagnosticsViewModel diagnostics, IBackgroundHost background, IFileDialogs dialogs, IAppRestarter restarter)
    {
        _core = core;
        _store = store;
        Categories = categories;
        _createImport = createImport;
        _exports = exports;
        _background = background;
        _dialogs = dialogs;
        _restarter = restarter;
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
    private void ChangeDataFolder()
    {
        FolderMessage = "";
        var picked = _dialogs.PickFolder(Res.Get("WizardBrowseTitle"), DataFolder);
        if (picked is null) return;
        var full = Path.GetFullPath(picked);
        if (string.Equals(full.TrimEnd('\\'), Path.GetFullPath(DataFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            FolderMessage = Res.Get("FolderSameAsNow");
            return;
        }

        _pendingCheck = FolderInspector.Inspect(full);
        PendingChecks.Clear();
        foreach (var line in FolderChecks.Describe(_pendingCheck)) PendingChecks.Add(line);
        PendingDataFolder = _pendingCheck.Path;
        CopyJournals = _pendingCheck.CanUse && !_pendingCheck.HasExistingData;   // an empty folder: bring the data along
        CanApplyDataFolder = _pendingCheck.CanUse;
    }

    [RelayCommand]
    private void CancelDataFolder()
    {
        PendingDataFolder = "";
        PendingChecks.Clear();
        CanApplyDataFolder = false;
    }

    [RelayCommand]
    private void ApplyDataFolder()
    {
        if (_pendingCheck is not { CanUse: true } check) return;
        try
        {
            var copied = 0;
            if (CopyJournals)
            {
                foreach (var file in Directory.GetFiles(DataFolder, JournalFiles.Prefix + "*" + JournalFiles.Extension))
                {
                    if (!JournalFiles.TryGetDeviceId(Path.GetFileName(file), out _)) continue;   // conflict copies stay behind
                    var target = Path.Combine(check.Path, Path.GetFileName(file));
                    if (File.Exists(target)) continue;                                           // never overwrite someone's journal
                    File.Copy(file, target);
                    copied++;
                }
            }

            AppLog.Info($"Data folder changed from '{DataFolder}' to '{check.Path}' ({copied} journal file(s) copied).");
            _store.Update(s => s with { DataFolder = check.Path, ResetLocalDatabase = true });
            FolderMessage = Res.Format("FolderChangedRestart", copied);
            RestartNeeded = true;
            CancelDataFolder();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Changing the data folder failed.", e);
            FolderMessage = Res.Format("FolderChangeFailed", e.Message);
        }
    }

    [RelayCommand]
    private void ChangeLocalFolder()
    {
        FolderMessage = "";
        var picked = _dialogs.PickFolder(Res.Get("LocalFolderPickTitle"), LocalFolder);
        if (picked is null) return;
        var full = Path.GetFullPath(picked);
        if (string.Equals(full.TrimEnd('\\'), Path.GetFullPath(LocalFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            FolderMessage = Res.Get("FolderSameAsNow");
            return;
        }
        if (FolderInspector.IsInOneDrive(full))
        {
            FolderMessage = Res.Get("LocalFolderNotInOneDrive");   // the cache database must stay on this computer
            return;
        }

        try
        {
            Directory.CreateDirectory(full);
            // keep this computer's identity; the database itself is rebuilt from the journals
            var identity = Path.Combine(LocalFolder, "device.json");
            var target = Path.Combine(full, "device.json");
            if (File.Exists(identity) && !File.Exists(target)) File.Copy(identity, target);

            AppLog.Info($"Local folder changed from '{LocalFolder}' to '{full}'.");
            _store.Update(s => s with { LocalDataFolder = full });
            FolderMessage = Res.Get("LocalFolderChangedRestart");
            RestartNeeded = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Changing the local folder failed.", e);
            FolderMessage = Res.Format("FolderChangeFailed", e.Message);
        }
    }

    [RelayCommand]
    private void RestartNow() => _restarter.Restart();

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
