using System.Collections.ObjectModel;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public enum CheckLevel { Ok, Warning, Error, Info }

public sealed record CheckLine(CheckLevel Level, string Text)
{
    public string Glyph => Level switch { CheckLevel.Ok => "✔", CheckLevel.Warning => "⚠", CheckLevel.Error => "✖", _ => "ℹ" };
}

public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoNext))] private string _folderPath = "";
    [ObservableProperty] private string _deviceName;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStep1)), NotifyPropertyChangedFor(nameof(IsStep2))] private int _step = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoNext))] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = "";

    public ObservableCollection<CheckLine> CheckLines { get; } = [];

    public FolderCheck? Check { get; private set; }
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool CanGoNext => !IsBusy && Check?.CanUse == true;
    public string? ProfileLabel => AppPaths.Profile;

    /// <summary>Set when the wizard finished successfully.</summary>
    public BudgetCore? Result { get; private set; }
    public AppSettings? ResultSettings { get; private set; }

    public event Action<bool>? CloseRequested;

    public FirstRunViewModel(AppSettings settings)
    {
        _settings = settings;
        _deviceName = settings.DeviceName ?? Environment.MachineName;
    }

    partial void OnFolderPathChanged(string value) => Inspect();

    private void Inspect()
    {
        Check = FolderInspector.Inspect(FolderPath);
        CheckLines.Clear();
        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            OnPropertyChanged(nameof(CanGoNext));
            return;
        }

        var c = Check;
        if (!c.Exists)
        {
            CheckLines.Add(new CheckLine(CheckLevel.Error, Res.Get("CheckFolderMissing")));
        }
        else
        {
            CheckLines.Add(new CheckLine(CheckLevel.Ok, Res.Get("CheckFolderExists")));
            CheckLines.Add(c.Writable
                ? new CheckLine(CheckLevel.Ok, Res.Get("CheckWritable"))
                : new CheckLine(CheckLevel.Error, Res.Get("CheckNotWritable")));
            CheckLines.Add(c.InOneDrive
                ? new CheckLine(CheckLevel.Ok, Res.Get("CheckOneDrive"))
                : new CheckLine(CheckLevel.Warning, Res.Get("CheckNoOneDrive")));
            CheckLines.Add(c.HasExistingData
                ? new CheckLine(CheckLevel.Info, Res.Format("CheckExistingData", c.JournalDevices.Count))
                : new CheckLine(CheckLevel.Info, Res.Get("CheckEmptyFolder")));
            if (c.CloudOnlyJournals > 0)
                CheckLines.Add(new CheckLine(CheckLevel.Warning, Res.Format("CheckCloudOnly", c.CloudOnlyJournals)));
            if (c.ConflictCopies > 0)
                CheckLines.Add(new CheckLine(CheckLevel.Warning, Res.Format("CheckConflicts", c.ConflictCopies)));
            if (c.Error is not null)
                CheckLines.Add(new CheckLine(CheckLevel.Error, c.Error));
        }
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext) return;
        Step = 2;
    }

    [RelayCommand]
    private void Back() => Step = 1;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (IsBusy || Check?.CanUse != true) return;
        var folder = Check.Path;
        var name = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var core = await Task.Run(() =>
            {
                Directory.CreateDirectory(AppPaths.LocalDir);
                var created = BudgetCore.Create(AppPaths.LocalDir, folder, name);
                try
                {
                    // The local database is only a view of the journals: start from a clean one.
                    created.Database.ClearMaterializedData();
                    var status = created.Sync.SyncOnce();

                    // Seeding on top of journals we could not read would overwrite the other person's edits of the same categories.
                    var readAll = !status.Issues.Any(i => i.Kind is SyncIssueKind.CloudOnlyFile or SyncIssueKind.ReadError
                        or SyncIssueKind.FolderUnavailable or SyncIssueKind.UpdateRequired);
                    if (readAll) created.Categories.SeedIfEmpty();
                    return created;
                }
                catch
                {
                    created.Dispose();
                    throw;
                }
            });

            ResultSettings = _settings with { DataFolder = folder, DeviceName = name };
            ResultSettings.Save();
            Result = core;
            CloseRequested?.Invoke(true);
        }
        catch (Exception e)
        {
            ErrorMessage = Res.Format("WizardFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
