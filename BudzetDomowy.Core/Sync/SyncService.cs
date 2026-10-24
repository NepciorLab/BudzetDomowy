using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Logging;
using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Sync;

public sealed class SyncOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Delay after a file system notification before syncing, to let OneDrive finish writing.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Overridable for tests; the default checks the OneDrive "recall" attributes.</summary>
    public Func<FileInfo, bool> IsCloudOnly { get; init; } = CloudFiles.IsCloudOnly;

    /// <summary>Where sync problems are recorded (new problems and recoveries, not every pass).</summary>
    public ILog? Log { get; init; }

    public SyncOptions WithLog(ILog log) => new() { PollInterval = PollInterval, Debounce = Debounce, IsCloudOnly = IsCloudOnly, Log = log };
}

public static class CloudFiles
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True for an "online only" placeholder whose content is not on this device.</summary>
    public static bool IsCloudOnly(FileInfo file)
    {
        try
        {
            file.Refresh();
            return (file.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }
        catch (IOException) { return false; }
    }
}

/// <summary>
/// Keeps the local database in step with all journals in the shared folder: periodic polling plus a
/// FileSystemWatcher. Reads only complete lines, remembers per-file offsets and never writes to other devices' files.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly string _folder;
    private readonly LocalDatabase _db;
    private readonly Materializer _materializer;
    private readonly LocalReader _reader;
    private readonly HybridLogicalClock _clock;
    private readonly SyncGuard _guard;
    private readonly SyncOptions _options;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _timerGate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private Timer? _debounceTimer;
    private bool _disposed;
    private volatile SyncStatus _status = new();
    private string _lastIssueSignature = "";

    public event EventHandler<SyncStatus>? StatusChanged;

    public SyncService(string folder, LocalDatabase db, Materializer materializer, LocalReader reader,
        HybridLogicalClock clock, SyncGuard? guard = null, SyncOptions? options = null)
    {
        _folder = folder;
        _db = db;
        _materializer = materializer;
        _reader = reader;
        _clock = clock;
        _guard = guard ?? new SyncGuard();
        _options = options ?? new SyncOptions();
    }

    public SyncStatus Status => _status;

    /// <summary>Starts the watcher and the polling timer and triggers an immediate sync.</summary>
    public void Start()
    {
        lock (_timerGate)
        {
            if (_disposed || _pollTimer is not null) return;
            TryStartWatcher();
            _debounceTimer = new Timer(_ => SyncSafe(), null, Timeout.Infinite, Timeout.Infinite);
            _pollTimer = new Timer(_ => SyncSafe(), null, TimeSpan.Zero, _options.PollInterval);
        }
    }

    public void Stop()
    {
        lock (_timerGate)
        {
            _pollTimer?.Dispose(); _pollTimer = null;
            _debounceTimer?.Dispose(); _debounceTimer = null;
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    /// <summary>Runs one synchronisation pass. If one is already running, waits for it and returns the current status.</summary>
    public SyncStatus SyncOnce()
    {
        _running.Wait();
        try { return RunPass(); }
        finally { _running.Release(); }
    }

    /// <summary>Drops the materialised data (keeping journals) and replays all journals from scratch.</summary>
    public SyncStatus Rebuild()
    {
        _running.Wait();
        try
        {
            _db.ClearMaterializedData();
            return RunPass();
        }
        finally { _running.Release(); }
    }

    private void SyncSafe()
    {
        try { if (!_disposed) SyncOnce(); }
        catch (Exception e) { Publish(_status with { State = SyncState.Problem, Issues = [new SyncIssue(SyncIssueKind.ReadError, null, e.Message)] }); }
    }

    private void TryStartWatcher()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            var w = new FileSystemWatcher(_folder, JournalFiles.Prefix + "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = false,
            };
            FileSystemEventHandler h = (_, _) => ScheduleSync();
            w.Changed += h; w.Created += h; w.Deleted += h;
            w.Renamed += (_, _) => ScheduleSync();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null; // polling still works
        }
    }

    private void ScheduleSync()
    {
        lock (_timerGate) _debounceTimer?.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
    }

    private SyncStatus RunPass()
    {
        Publish(_status with { State = SyncState.Syncing });

        var issues = new List<SyncIssue>();
        var total = ApplyResult.Empty;

        if (!Directory.Exists(_folder))
        {
            issues.Add(new SyncIssue(SyncIssueKind.FolderUnavailable, null, $"Folder '{_folder}' is not available."));
            return Finish(issues, total, success: false);
        }

        string[] files;
        try { files = Directory.GetFiles(_folder, JournalFiles.Prefix + "*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SyncIssue(SyncIssueKind.FolderUnavailable, null, e.Message));
            return Finish(issues, total, success: false);
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (!JournalFiles.TryGetDeviceId(name, out var deviceId))
            {
                if (JournalFiles.LooksLikeConflictCopy(name))
                    issues.Add(new SyncIssue(SyncIssueKind.ConflictCopy, name, $"'{name}' looks like a OneDrive conflict copy and was not read."));
                continue;
            }

            total = total.Add(SyncFile(path, name, deviceId, issues));
        }

        _guard.UpdateRequired = issues.Any(i => i.Kind == SyncIssueKind.UpdateRequired);
        return Finish(issues, total, success: true);
    }

    private ApplyResult SyncFile(string path, string name, string deviceId, List<SyncIssue> issues)
    {
        try
        {
            var info = new FileInfo(path);
            if (_options.IsCloudOnly(info))
            {
                issues.Add(new SyncIssue(SyncIssueKind.CloudOnlyFile, name,
                    $"'{name}' is stored only in the cloud. Set the folder to \"Always keep on this device\"."));
                return ApplyResult.Empty;
            }

            var offset = _materializer.GetOffset(name);
            if (info.Length == offset) return ApplyResult.Empty;

            var read = JournalReader.ReadFrom(path, offset);
            var start = read.FileShrank ? 0 : offset;

            var result = ApplyResult.Empty;
            if (read.Events.Count > 0 || read.NewOffset != offset)
            {
                result = _materializer.Apply(read.Events, new FileProgress(name, deviceId, start, read.NewOffset));
                foreach (var e in read.Events) _clock.Observe(e.Timestamp);
            }

            if (read.CorruptLines > 0)
                issues.Add(new SyncIssue(SyncIssueKind.CorruptLines, name, $"{read.CorruptLines} invalid line(s) skipped in '{name}'.", read.CorruptLines));
            if (result.Failed > 0)
                issues.Add(new SyncIssue(SyncIssueKind.FailedEvents, name, $"{result.Failed} event(s) from '{name}' could not be applied.", result.Failed));
            if (read.UnsupportedSchemaVersion is { } v)
                issues.Add(new SyncIssue(SyncIssueKind.UpdateRequired, name,
                    $"'{name}' uses data schema v{v}; this app supports v{JournalEvent.CurrentSchemaVersion}. Update the application."));
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SyncIssue(SyncIssueKind.ReadError, name, $"'{name}' could not be read: {e.Message}"));
            return ApplyResult.Empty;
        }
    }

    private SyncStatus Finish(List<SyncIssue> issues, ApplyResult applied, bool success)
    {
        IReadOnlyList<DeviceJournalInfo> devices;
        try { devices = _reader.GetJournalInfos(); }
        catch (Exception) { devices = _status.Devices; }

        LogChanges(issues, applied);

        var status = new SyncStatus
        {
            State = issues.Count == 0 ? SyncState.Synced : SyncState.Problem,
            LastSyncUtc = success ? DateTimeOffset.UtcNow : _status.LastSyncUtc,
            Issues = issues,
            Devices = devices,
            LastApplied = applied,
        };
        Publish(status);
        return status;
    }

    private void LogChanges(List<SyncIssue> issues, ApplyResult applied)
    {
        var log = _options.Log;
        if (log is null) return;

        var signature = string.Join('|', issues.Select(i => $"{i.Kind}:{i.FileName}:{i.Count}").OrderBy(x => x, StringComparer.Ordinal));
        if (signature != _lastIssueSignature)
        {
            if (issues.Count == 0) log.Info("Synchronization problems are resolved.");
            foreach (var i in issues) log.Warn($"Sync issue {i.Kind}: {i.Message}");
            _lastIssueSignature = signature;
        }
        if (applied.Applied > 0) log.Info($"Synchronization applied {applied.Applied} event(s) from journals ({applied.Ignored} already known, {applied.Failed} failed).");
    }

    private void Publish(SyncStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
