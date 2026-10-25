using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using System.Collections.ObjectModel;
using BudzetDomowy.App.Infrastructure.Updates;
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

public sealed record StartPageOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record ScaleOption(UiScaleChoice Choice, string Label)
{
    public override string ToString() => Label;
}

public sealed record MarkerOption(MarkerShape Choice, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of the shortcuts list; those with an <see cref="Id"/> can be given other keys.</summary>
public sealed partial class ShortcutRow : ObservableObject
{
    public string? Id { get; }
    public string Description { get; }
    public bool IsFixed => Id is null;

    [ObservableProperty] private string _keys;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanReset))] private bool _isCustom;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _message = "";

    public bool CanReset => IsCustom;

    public ShortcutRow(string? id, string keys, string description)
    {
        Id = id;
        _keys = keys;
        Description = description;
        if (id is not null) _isCustom = ShortcutMap.Instance.IsCustom(id);
    }

    public void Reload()
    {
        if (Id is null) return;
        Keys = ShortcutMap.Instance.DisplayOf(Id);
        IsCustom = ShortcutMap.Instance.IsCustom(Id);
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public const int AppearanceTab = 1;
    public const int BackupTab = 5;
    public const int ShortcutsTab = 6;

    private readonly BudgetCore _core;
    private readonly SettingsStore _store;
    private readonly Func<ImportViewModel> _createImport;
    private readonly ExportActions _exports;
    private readonly IBackgroundHost _background;
    private readonly IFileDialogs _dialogs;
    private readonly IAppRestarter _restarter;
    private readonly UpdateService _updates;
    private ReleaseInfo? _foundRelease;
    private FolderCheck? _pendingCheck;
    private bool _restoringBackground;

    public CategoriesViewModel Categories { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public BackupViewModel Backup { get; }

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
    // ----- updates from GitHub -----
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _updateMessage = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updateBusy;

    public string CurrentVersionText => _updates.Current.ToString(3);
    public string UpdateRepository => _store.Current.UpdateRepository;

    partial void OnAutoUpdateChanged(bool value) => _store.Update(s => s with { AutoUpdate = value });

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateBusy = true;
        UpdateAvailable = false;
        UpdateMessage = Res.Get("UpdateChecking");
        try
        {
            var check = await _updates.CheckAsync();
            _foundRelease = check.Release;
            UpdateAvailable = check.State == UpdateState.Available;
            UpdateMessage = check.Message;
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_foundRelease is not { } release) return;
        UpdateBusy = true;
        UpdateMessage = Res.Get("UpdateDownloading");
        try
        {
            var error = await _updates.InstallAsync(release);
            if (error is not null)
            {
                UpdateMessage = error;
                return;
            }
            UpdateMessage = Res.Get("UpdateInstalled");
            _updates.Restart();
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [ObservableProperty] private bool _backgroundMode;
    [ObservableProperty] private string _backgroundMessage = "";

    /// <summary>The program's keys: first the fixed ones, then those that can be changed.</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts { get; } =
    [
        new(null, "Enter", Res.Get("KeyEnter")),
        new(null, "Ctrl + Enter", Res.Get("KeyCtrlEnter")),
        new(null, "Tab / Shift + Tab", Res.Get("KeyTab")),
        new(null, "Esc", Res.Get("KeyEsc")),
        new(null, "Esc (" + Res.Get("KeyInTable") + ")", Res.Get("KeyEscTable")),
        .. ShortcutMap.Definitions.Select(d => new ShortcutRow(d.Id, ShortcutMap.Instance.DisplayOf(d.Id), ShortcutMap.Describe(d))),
    ];

    [RelayCommand]
    private void EditShortcut(ShortcutRow? row)
    {
        if (row is null || row.IsFixed) return;
        foreach (var other in Shortcuts) other.IsEditing = false;
        row.Message = "";
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelShortcutEdit(ShortcutRow? row)
    {
        if (row is not null) row.IsEditing = false;
    }

    /// <summary>The view passes the key that was pressed while a row waits for its new shortcut.</summary>
    public void ApplyShortcut(ShortcutRow row, System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (row.Id is not { } id || ShortcutMap.IsModifier(key)) return;
        var error = ShortcutMap.Instance.Set(id, key, modifiers);
        row.Message = error ?? "";
        if (error is not null) return;

        row.IsEditing = false;
        row.Reload();
        if (id == ShortcutMap.GlobalQuickAdd && _background.IsActive)
        {
            // the global key is registered with Windows: register it again under the new keys
            _background.Stop();
            if (!_background.Start(out var problem)) BackgroundMessage = problem ?? "";
        }
    }

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row?.Id is not { } id) return;
        ShortcutMap.Instance.Reset(id);
        row.Message = "";
        row.IsEditing = false;
        row.Reload();
        if (id == ShortcutMap.GlobalQuickAdd && _background.IsActive)
        {
            _background.Stop();
            _background.Start(out _);
        }
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeChoice.System, Res.Get("ThemeSystem")),
        new(ThemeChoice.Light, Res.Get("ThemeLight")),
        new(ThemeChoice.Dark, Res.Get("ThemeDark")),
    ];

    [ObservableProperty] private ThemeOption _selectedTheme;

    public IReadOnlyList<ScaleOption> Scales { get; } =
    [
        new(UiScaleChoice.Small, Res.Get("ScaleSmall")),
        new(UiScaleChoice.Medium, Res.Get("ScaleMedium")),
        new(UiScaleChoice.Large, Res.Get("ScaleLarge")),
    ];

    public IReadOnlyList<MarkerOption> Markers { get; } =
    [
        new(MarkerShape.Circle, Res.Get("MarkerCircle")),
        new(MarkerShape.Square, Res.Get("MarkerSquare")),
        new(MarkerShape.Bar, Res.Get("MarkerBar")),
    ];

    /// <summary>"Last used" first, then every screen in the order of the navigation list.</summary>
    public IReadOnlyList<StartPageOption> StartPages { get; } =
    [
        new("last", Res.Get("StartPageLast")),
        .. ShortcutMap.Pages.Select(p => new StartPageOption(p, Res.Get("Nav" + char.ToUpperInvariant(p[0]) + p[1..]))),
    ];

    [ObservableProperty] private StartPageOption _selectedStartPage;

    partial void OnSelectedStartPageChanged(StartPageOption value) => _store.Update(s => s with { StartPage = value.Key });

    [ObservableProperty] private ScaleOption _selectedScale;
    [ObservableProperty] private MarkerOption _selectedMarker;

    partial void OnSelectedScaleChanged(ScaleOption value) => UiPreferences.Instance.Scale = value.Choice;
    partial void OnSelectedMarkerChanged(MarkerOption value) => UiPreferences.Instance.Marker = value.Choice;

    /// <summary>The amounts as they will look, for the preview in the appearance tab.</summary>
    public string SampleExpense => UiPreferences.Instance.SignOf(false) + (1234.5m).ToString("C2", System.Globalization.CultureInfo.CurrentCulture);
    public string SampleIncome => UiPreferences.Instance.SignOf(true) + (6500m).ToString("C2", System.Globalization.CultureInfo.CurrentCulture);

    public string DataFolder => _core.DataFolder;
    public string LocalFolder => AppPaths.LocalDir;
    public string DeviceName => _core.Device.DeviceName;
    public string DeviceId => _core.Device.DeviceId;

    public SettingsViewModel(BudgetCore core, SettingsStore store, CategoriesViewModel categories,
        Func<ImportViewModel> createImport, ExportActions exports, DiagnosticsViewModel diagnostics, IBackgroundHost background, IFileDialogs dialogs, IAppRestarter restarter, UpdateService updates, BackupViewModel backup)
    {
        _core = core;
        _store = store;
        Categories = categories;
        _createImport = createImport;
        _exports = exports;
        _background = background;
        _dialogs = dialogs;
        _restarter = restarter;
        _updates = updates;
        Backup = backup;
        _autoUpdate = store.Current.AutoUpdate;
        Diagnostics = diagnostics;
        _backgroundMode = store.Current.BackgroundMode && background.IsActive;
        _selectedTheme = Themes.First(t => t.Choice == store.Current.Theme);
        _selectedStartPage = StartPages.FirstOrDefault(p => p.Key == store.Current.StartPage) ?? StartPages[1];
        _selectedScale = Scales.First(s => s.Choice == store.Current.UiScale);
        _selectedMarker = Markers.First(m => m.Choice == store.Current.CategoryMarker);
        UiPreferences.Instance.OnAppearanceChanged(this, self =>
        {
            self.OnPropertyChanged(nameof(SampleExpense));
            self.OnPropertyChanged(nameof(SampleIncome));
        });
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
