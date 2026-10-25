using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Backup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record BackupRow(BackupInfo Info)
{
    public string DateText => Info.CreatedLocal.ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    public string KindText => Res.Get(Info.Kind switch
    {
        BackupKind.Manual => "BackupKindManual",
        BackupKind.BeforeRestore => "BackupKindBeforeRestore",
        _ => "BackupKindAutomatic",
    });

    public string SizeText => Info.SizeBytes < 1024 * 1024
        ? (Info.SizeBytes / 1024.0).ToString("N0", CultureInfo.CurrentCulture) + " KB"
        : (Info.SizeBytes / 1024.0 / 1024.0).ToString("N1", CultureInfo.CurrentCulture) + " MB";

    public int Files => Info.JournalFiles;
}

/// <summary>The "BackUp" tab of Settings, and the daily copy the program makes by itself.</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly DataHub _hub;
    private readonly IMessageDialogs _dialogs;
    private DispatcherTimer? _timer;

    public ObservableCollection<BackupRow> Items { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRestore))] private BackupRow? _selected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRestore))] private bool _isBusy;

    public string Folder => _core.Backups.BackupFolder;
    public int Keep => _core.Backups.Keep;
    public string Intro => Res.Format("BackupIntro", Keep);
    public bool CanRestore => Selected is not null && !IsBusy;
    public bool HasItems => Items.Count > 0;

    public BackupViewModel(BudgetCore core, DataHub hub, IMessageDialogs dialogs)
    {
        _core = core;
        _hub = hub;
        _dialogs = dialogs;
        Reload();
    }

    public void Reload()
    {
        var keep = Selected?.Info.Path;
        Items.Clear();
        foreach (var info in _core.Backups.List()) Items.Add(new BackupRow(info));
        Selected = keep is null ? null : Items.FirstOrDefault(r => r.Info.Path == keep);
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>First check shortly after the start (the data had time to synchronise), then every few hours: one copy per day.</summary>
    public void StartDaily(TimeSpan firstDelay, TimeSpan every)
    {
        _timer?.Stop();
        var first = true;
        _timer = new DispatcherTimer { Interval = firstDelay };
        _timer.Tick += async (_, _) =>
        {
            if (first)
            {
                first = false;
                _timer!.Interval = every;
            }
            await EnsureDailyAsync();
        };
        _timer.Start();
    }

    public async Task<BackupInfo?> EnsureDailyAsync()
    {
        try
        {
            var made = await Task.Run(() => _core.Backups.EnsureDaily());
            if (made is not null)
            {
                AppLog.Info($"Daily backup made: {made.FileName}");
                Reload();
            }
            return made;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Error("The daily backup failed.", e);   // never a reason to disturb the user's work
            return null;
        }
    }

    [RelayCommand]
    private async Task CreateNowAsync()
    {
        IsBusy = true;
        Message = Res.Get("BackupCreating");
        try
        {
            var made = await Task.Run(() => _core.Backups.Create(BackupKind.Manual));
            Message = made is null ? Res.Get("BackupNothing") : Res.Format("BackupCreated", made.CreatedLocal.ToString("g", CultureInfo.CurrentCulture));
            Reload();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Creating a backup failed.", e);
            Message = Res.Format("BackupFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Selected is not { } row) return;
        var when = row.Info.CreatedLocal.ToString("f", CultureInfo.CurrentCulture);
        if (!_dialogs.Confirm(Res.Get("BackupRestoreTitle"), Res.Format("BackupRestoreQuestion", when))) return;

        IsBusy = true;
        Message = Res.Get("BackupRestoring");
        try
        {
            await Task.Run(() => _core.RestoreBackup(row.Info));
            Message = Res.Format("BackupRestored", when);
            _hub.Request();                                    // every screen shows the restored data
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Error("Restoring a backup failed.", e);
            Message = Res.Format("BackupRestoreFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }
}
