using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record DeviceLine(string Name, string Id, int EventCount, string LastRead, bool IsThisDevice);

public sealed partial class SyncDetailsViewModel : ObservableObject
{
    private readonly BudgetCore _core;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<string> Issues { get; } = [];
    public ObservableCollection<DeviceLine> Devices { get; } = [];
    public string DataFolder => _core.DataFolder;
    public string LocalFolder => AppPaths.LocalDir;
    public string ThisDevice => $"{_core.Device.DeviceName} ({_core.Device.DeviceId})";

    public SyncDetailsViewModel(BudgetCore core)
    {
        _core = core;
        Update(core.Sync.Status);
        core.Sync.StatusChanged += OnStatus;
    }

    public void Detach() => _core.Sync.StatusChanged -= OnStatus;

    private void OnStatus(object? sender, SyncStatus s) => Application.Current.Dispatcher.BeginInvoke(() => Update(s));

    private void Update(SyncStatus s)
    {
        Summary = s.State switch
        {
            SyncState.Syncing => Res.Get("SyncRunning"),
            SyncState.Synced => Res.Format("SyncDetailsDone", (s.LastSyncUtc ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("G", CultureInfo.CurrentCulture)),
            SyncState.Problem => Res.Get("SyncDetailsProblem"),
            _ => Res.Get("SyncWaiting"),
        };

        Issues.Clear();
        foreach (var issue in s.Issues) Issues.Add(Describe(issue));
        if (s.Issues.Count == 0) Issues.Add(Res.Get("SyncNoIssues"));

        Devices.Clear();
        foreach (var d in s.Devices)
        {
            var mine = string.Equals(d.DeviceId, _core.Device.DeviceId, StringComparison.OrdinalIgnoreCase);
            Devices.Add(new DeviceLine(
                mine ? _core.Device.DeviceName + " " + Res.Get("ThisComputer") : Res.Get("OtherComputer"),
                d.DeviceId, d.EventCount,
                d.LastReadUtc is { } t ? t.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) : "—", mine));
        }
    }

    private string Describe(SyncIssue i) => i.Kind switch
    {
        SyncIssueKind.FolderUnavailable => Res.Format("IssueFolderUnavailable", _core.DataFolder),
        SyncIssueKind.CloudOnlyFile => Res.Format("IssueCloudOnly", i.FileName),
        SyncIssueKind.ConflictCopy => Res.Format("IssueConflict", i.FileName),
        SyncIssueKind.UpdateRequired => Res.Format("IssueUpdateRequired", i.FileName),
        SyncIssueKind.ReadError => Res.Format("IssueReadError", i.FileName ?? "", i.Message),
        SyncIssueKind.CorruptLines => Res.Format("IssueCorrupt", i.Count, i.FileName),
        SyncIssueKind.FailedEvents => Res.Format("IssueFailedEvents", i.Count, i.FileName),
        _ => i.Message,
    };

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await Task.Run(() => _core.Sync.SyncOnce()); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(DataFolder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataFolder) { UseShellExecute = true });
    }
}
