using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record DeviceDiagnostic(string Name, string Id, int EventCount, string JournalSize, string LastRead);

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly DataHub _hub;

    public ObservableCollection<DeviceDiagnostic> Devices { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _databaseInfo = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRebuild))] private bool _isBusy;

    public string Version { get; } =
        typeof(DiagnosticsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    public string DataFolder => _core.DataFolder;
    public string LogFolder => AppLog.Directory;
    public string ThisDevice => $"{_core.Device.DeviceName} ({_core.Device.DeviceId})";
    public bool CanRebuild => !IsBusy;

    public DiagnosticsViewModel(BudgetCore core, ToastService toast, DataHub hub)
    {
        _core = core;
        _toast = toast;
        _hub = hub;
        core.Sync.StatusChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var s = _core.Sync.Status;
        Summary = s.State switch
        {
            SyncState.Synced => Res.Format("SyncDetailsDone", (s.LastSyncUtc ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("G", CultureInfo.CurrentCulture)),
            SyncState.Problem => Res.Get("SyncDetailsProblem"),
            SyncState.Syncing => Res.Get("SyncRunning"),
            _ => Res.Get("SyncWaiting"),
        };

        Issues.Clear();
        foreach (var i in s.Issues) Issues.Add(SyncTexts.Describe(i, _core.DataFolder));
        if (s.Issues.Count == 0) Issues.Add(Res.Get("SyncNoIssues"));

        Devices.Clear();
        foreach (var d in s.Devices)
        {
            var mine = string.Equals(d.DeviceId, _core.Device.DeviceId, StringComparison.OrdinalIgnoreCase);
            Devices.Add(new DeviceDiagnostic(
                mine ? _core.Device.DeviceName + " " + Res.Get("ThisComputer") : Res.Get("OtherComputer"),
                d.DeviceId, d.EventCount, SizeOf(Path.Combine(_core.DataFolder, d.FileName)),
                d.LastReadUtc is { } t ? t.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) : "—"));
        }

        DatabaseInfo = $"{_core.Database.Path} ({SizeOf(_core.Database.Path)})";
    }

    private static string SizeOf(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length < 1024 * 1024 ? $"{length / 1024.0:0.#} KB" : $"{length / 1024.0 / 1024.0:0.#} MB";
        }
        catch (IOException) { return "—"; }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        IsBusy = true;
        try { await Task.Run(() => _core.Sync.SyncOnce()); }
        finally { IsBusy = false; }
        Refresh();
    }

    /// <summary>Throws the local database away and replays every journal. Safe, because the journals are the real data.</summary>
    [RelayCommand]
    private async Task RebuildAsync()
    {
        Message = "";
        if (SyncTexts.BlocksRebuild(_core.Sync.Status))
        {
            Message = Res.Get("DiagRebuildBlocked");
            return;
        }

        IsBusy = true;
        try
        {
            AppLog.Info("Rebuilding the local database from the journals (requested by the user).");
            var status = await Task.Run(() => _core.Sync.Rebuild());
            var events = status.Devices.Sum(d => d.EventCount);
            Message = Res.Format("DiagRebuildDone", events);
            _toast.Show(Message);
            _hub.Request();
        }
        catch (Exception e)
        {
            AppLog.Error("Rebuilding the local database failed.", e);
            Message = Res.Get("DiagRebuildFailed");
        }
        finally
        {
            IsBusy = false;
        }
        Refresh();
    }

    [RelayCommand]
    private void OpenLogs() => Open(LogFolder);

    [RelayCommand]
    private void OpenData() => Open(DataFolder);

    private static void Open(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not open folder " + path + ": " + e.Message);
        }
    }
}
