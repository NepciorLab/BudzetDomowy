using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BudzetDomowy.Core.Backup;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;

namespace BudzetDomowy.Core.Corrections;

/// <summary>What was written for one transaction; for an update the transaction as it was, so that the correction can be undone.</summary>
public sealed class RecordedChange
{
    public ChangeKind Kind { get; init; }
    public Guid TransactionId { get; init; }
    public JsonNode? Before { get; init; }
}

public sealed class DayRow
{
    public DateOnly Date { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public int Count { get; init; }
}

public sealed class DayCheckRow
{
    public DateOnly Date { get; init; }
    public DayRow Before { get; init; } = new();
    public DayRow After { get; init; } = new();
    public DayRow Expected { get; init; } = new();
    public bool Ok { get; init; }
}

/// <summary>A correction that was applied: kept in the data folder (folder "Korekty") as proof of what was done and to undo it.</summary>
public sealed class CorrectionRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
    public string Title { get; init; } = "";
    public string DeviceName { get; init; } = "";
    /// <summary>The backup made right before the correction (file name in the backup folder); empty when none could be made.</summary>
    public string BackupFile { get; init; } = "";
    public List<RecordedChange> Changes { get; init; } = [];
    /// <summary>The days that were expected to change or did change, as they were checked.</summary>
    public List<DayCheckRow> CheckedDays { get; init; } = [];
    public int DaysChecked { get; init; }
    public int ProblemDays { get; init; }
    public bool AllOk { get; init; }
    public bool Reverted { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>File name inside the "Korekty" folder.</summary>
    public string FileName => $"korekta_{AppliedAt.ToLocalTime():yyyyMMdd-HHmmss}_{Id.ToString("N")[..8]}.json";
}

public sealed record CorrectionResult(CorrectionRecord Record, VerificationReport Report);

/// <summary>The data changed after the plan was made (another computer, or the main program): nothing was written.</summary>
public sealed class CorrectionConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Applies a plan to the real data as ordinary events (they reach every computer like anything typed by hand), checks the result day by
/// day and can undo it. Before writing, a backup of the journals is made; afterwards a record of the correction is kept in the data folder.
/// </summary>
public sealed class CorrectionRunner
{
    public const string FolderName = "Korekty";

    private readonly BudgetCore _core;
    private readonly Func<DateTimeOffset> _now;

    public CorrectionRunner(BudgetCore core, Func<DateTimeOffset>? now = null)
    {
        _core = core;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Folder => Path.Combine(_core.DataFolder, FolderName);

    private static string Fingerprint(Transaction t) => JournalJson.ToPayload(t).ToJsonString();

    /// <summary>
    /// Writes the plan, then compares every day with the state before. Nothing is written when the transactions are no longer as the
    /// plan saw them (<see cref="CorrectionConflictException"/>).
    /// </summary>
    public CorrectionResult Apply(CorrectionPlan plan)
    {
        if (plan.IsEmpty) throw new InvalidOperationException("The plan changes nothing.");
        _core.Sync.SyncOnce();
        var current = _core.Reader.GetTransactions().ToDictionary(t => t.Id);

        foreach (var c in plan.Changes)
        {
            if (c.Kind == ChangeKind.Update)
            {
                if (!current.TryGetValue(c.Before!.Id, out var now) || Fingerprint(now) != Fingerprint(c.Before))
                    throw new CorrectionConflictException($"A transaction of {c.Before.Date:yyyy-MM-dd} was changed since the plan was made.");
            }
            else if (current.ContainsKey(c.After.Id))
            {
                throw new CorrectionConflictException("The plan was already applied.");
            }
        }

        var before = DaySnapshot.Build(current.Values);
        var backup = _core.Backups.Create(BackupKind.Manual);

        foreach (var c in plan.Changes) _core.Recorder.Save(c.After);

        var after = DaySnapshot.Build(_core.Reader.GetTransactions());
        var report = Verifier.Compare(before, after, plan.ExpectedDeltas());

        var record = new CorrectionRecord
        {
            Id = Guid.NewGuid(),
            AppliedAt = _now(),
            Title = plan.Title,
            DeviceName = _core.Device.DeviceName,
            BackupFile = backup?.FileName ?? "",
            Changes = plan.Changes.Select(c => new RecordedChange
            {
                Kind = c.Kind,
                TransactionId = c.After.Id,
                Before = c.Before is null ? null : JournalJson.ToPayload(c.Before),
            }).ToList(),
            CheckedDays = report.Days.Select(ToRow).ToList(),
            DaysChecked = report.DaysChecked,
            ProblemDays = report.Problems.Count,
            AllOk = report.AllOk,
        };
        Save(record);
        return new CorrectionResult(record, report);
    }

    /// <summary>The corrections made so far, the newest first.</summary>
    public IReadOnlyList<CorrectionRecord> History()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<CorrectionRecord>();
        foreach (var file in Directory.GetFiles(Folder, "korekta_*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CorrectionRecord>(File.ReadAllText(file), JournalJson.Options) is { } record) list.Add(record);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* a damaged record is skipped */ }
        }
        return list.OrderByDescending(r => r.AppliedAt).ToList();
    }

    /// <summary>
    /// Writes the earlier versions back and deletes what the correction added, as new events (so every computer follows without
    /// rebuilding), then checks the days: the change must be exactly the reverse of what the correction did to the transactions now.
    /// </summary>
    public VerificationReport Revert(CorrectionRecord record)
    {
        if (record.Reverted) throw new InvalidOperationException("The correction was already undone.");
        _core.Sync.SyncOnce();
        var current = _core.Reader.GetTransactions().ToDictionary(t => t.Id);
        var before = DaySnapshot.Build(current.Values);

        var expected = new Dictionary<DateOnly, DaySum>();
        void Expect(DateOnly date, DaySum change) => expected[date] = expected.GetValueOrDefault(date, DaySum.Empty) + change;
        var restores = new List<Transaction>();
        var removals = new List<Guid>();

        foreach (var c in record.Changes)
        {
            current.TryGetValue(c.TransactionId, out var now);
            if (c.Kind == ChangeKind.Update)
            {
                var original = JournalJson.FromPayload<Transaction>(c.Before) ?? throw new InvalidOperationException("The record has no earlier version.");
                restores.Add(original);
                if (now is not null) Expect(now.Date, DaySum.Empty - DaySum.Of(now));
                Expect(original.Date, DaySum.Of(original));
            }
            else if (now is not null)
            {
                removals.Add(now.Id);
                Expect(now.Date, DaySum.Empty - DaySum.Of(now));
            }
        }

        foreach (var t in restores) _core.Recorder.Save(t);
        foreach (var id in removals) _core.Recorder.Delete(EntityTypes.Transaction, id);

        var after = DaySnapshot.Build(_core.Reader.GetTransactions());
        var report = Verifier.Compare(before, after, expected.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value));

        record.Reverted = true;
        record.RevertedAt = _now();
        Save(record);
        return report;
    }

    /// <summary>Turns a saved record's days back into a report to show or export.</summary>
    public static VerificationReport ReportOf(CorrectionRecord record)
    {
        static DaySum Sum(DayRow r) => new(r.Income, r.Expense, r.Count);
        var days = record.CheckedDays.Select(d => new DayCheck(d.Date, Sum(d.Before), Sum(d.After), Sum(d.Expected))).ToList();
        var before = days.Aggregate(DaySum.Empty, (a, d) => a + d.Before);
        var after = days.Aggregate(DaySum.Empty, (a, d) => a + d.After);
        var expected = days.Aggregate(DaySum.Empty, (a, d) => a + d.Expected);
        return new VerificationReport(days, record.DaysChecked, before, after, expected);
    }

    private static DayCheckRow ToRow(DayCheck d) => new()
    {
        Date = d.Date, Before = ToRow(d.Before, d.Date), After = ToRow(d.After, d.Date), Expected = ToRow(d.Expected, d.Date), Ok = d.Ok,
    };

    private static DayRow ToRow(DaySum s, DateOnly date) => new() { Date = date, Income = s.Income, Expense = s.Expense, Count = s.Count };

    private void Save(CorrectionRecord record)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, record.FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions(JournalJson.Options) { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
