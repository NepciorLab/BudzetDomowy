using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Plain-language descriptions of synchronisation problems, with what the user should do about them.</summary>
public static class SyncTexts
{
    public static string Describe(SyncIssue i, string dataFolder) => i.Kind switch
    {
        SyncIssueKind.FolderUnavailable => Res.Format("IssueFolderUnavailable", dataFolder),
        SyncIssueKind.CloudOnlyFile => Res.Format("IssueCloudOnly", i.FileName),
        SyncIssueKind.ConflictCopy => Res.Format("IssueConflict", i.FileName),
        SyncIssueKind.UpdateRequired => Res.Format("IssueUpdateRequired", i.FileName),
        SyncIssueKind.ReadError => Res.Format("IssueReadError", i.FileName ?? "", i.Message),
        SyncIssueKind.CorruptLines => Res.Format("IssueCorrupt", i.Count, i.FileName),
        SyncIssueKind.FailedEvents => Res.Format("IssueFailedEvents", i.Count, i.FileName),
        _ => i.Message,
    };

    /// <summary>Problems after which rebuilding the local database from the journals would lose data from view.</summary>
    public static bool BlocksRebuild(SyncStatus s) =>
        s.Issues.Any(i => i.Kind is SyncIssueKind.CloudOnlyFile or SyncIssueKind.ReadError or SyncIssueKind.FolderUnavailable or SyncIssueKind.UpdateRequired);
}
