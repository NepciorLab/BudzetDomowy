using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Sync;

public enum SyncState { NotStarted, Syncing, Synced, Problem }

public enum SyncIssueKind
{
    /// <summary>The synchronised folder does not exist or cannot be listed.</summary>
    FolderUnavailable,
    /// <summary>A journal is an "online only" OneDrive placeholder: the user must choose "Always keep on this device".</summary>
    CloudOnlyFile,
    /// <summary>A journal-like file with a OneDrive conflict suffix was found and is not read.</summary>
    ConflictCopy,
    /// <summary>A journal uses a newer schema than this app understands: the app must be updated.</summary>
    UpdateRequired,
    /// <summary>A journal could not be read (locked, I/O error) and will be retried.</summary>
    ReadError,
    /// <summary>Some complete lines in a journal are not valid events and were skipped.</summary>
    CorruptLines,
    /// <summary>Some events could not be applied (unknown entity type or invalid payload).</summary>
    FailedEvents,
}

public sealed record SyncIssue(SyncIssueKind Kind, string? FileName, string Message, int Count = 1);

public sealed record SyncStatus
{
    public SyncState State { get; init; } = SyncState.NotStarted;
    public DateTimeOffset? LastSyncUtc { get; init; }
    public IReadOnlyList<SyncIssue> Issues { get; init; } = [];
    public IReadOnlyList<DeviceJournalInfo> Devices { get; init; } = [];
    public ApplyResult LastApplied { get; init; } = ApplyResult.Empty;
    public bool UpdateRequired => Issues.Any(i => i.Kind == SyncIssueKind.UpdateRequired);
}

/// <summary>Shared flag: when a newer schema is met, local writes are refused so old code cannot damage newer data.</summary>
public sealed class SyncGuard
{
    private volatile bool _updateRequired;

    public bool UpdateRequired
    {
        get => _updateRequired;
        internal set => _updateRequired = value;
    }

    public void ThrowIfBlocked()
    {
        if (_updateRequired) throw new UpdateRequiredException();
    }
}

public sealed class UpdateRequiredException : InvalidOperationException
{
    public UpdateRequiredException()
        : base("The data folder contains data written by a newer version of the application. Update the application.") { }
}
