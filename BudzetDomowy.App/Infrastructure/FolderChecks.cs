using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Plain-language findings about a candidate data folder (first-run wizard and "change folder" in Settings).</summary>
public static class FolderChecks
{
    public static IReadOnlyList<CheckLine> Describe(FolderCheck c)
    {
        var lines = new List<CheckLine>();
        if (!c.Exists)
        {
            lines.Add(new CheckLine(CheckLevel.Error, Res.Get("CheckFolderMissing")));
            return lines;
        }

        lines.Add(new CheckLine(CheckLevel.Ok, Res.Get("CheckFolderExists")));
        lines.Add(c.Writable ? new CheckLine(CheckLevel.Ok, Res.Get("CheckWritable")) : new CheckLine(CheckLevel.Error, Res.Get("CheckNotWritable")));
        lines.Add(c.InOneDrive ? new CheckLine(CheckLevel.Ok, Res.Get("CheckOneDrive")) : new CheckLine(CheckLevel.Warning, Res.Get("CheckNoOneDrive")));
        lines.Add(c.HasExistingData
            ? new CheckLine(CheckLevel.Info, Res.Format("CheckExistingData", c.JournalDevices.Count))
            : new CheckLine(CheckLevel.Info, Res.Get("CheckEmptyFolder")));
        if (c.CloudOnlyJournals > 0) lines.Add(new CheckLine(CheckLevel.Warning, Res.Format("CheckCloudOnly", c.CloudOnlyJournals)));
        if (c.ConflictCopies > 0) lines.Add(new CheckLine(CheckLevel.Warning, Res.Format("CheckConflicts", c.ConflictCopies)));
        if (c.Error is not null) lines.Add(new CheckLine(CheckLevel.Error, c.Error));
        return lines;
    }
}
