using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Corrections;

/// <summary>The corrections up to the chosen one cannot be rebuilt from the data (the record comes from an older version of the tool).</summary>
public sealed class AuditNotSupportedException(string message) : InvalidOperationException(message);

/// <summary>The monthly check of the whole history of corrections up to one of them.</summary>
public sealed record MonthlyAuditResult(VerificationReport Report, int CorrectionsIncluded, int UndosIncluded, string UpToTitle);

/// <summary>
/// Checks, month by month, what the corrections up to a chosen one did to the whole data. Two states are rebuilt from the journals:
/// the state without any event of the tool ("before any correction at all"), and the state with the events of the tool up to the end
/// of the chosen correction. The difference of every month must be exactly what the records say these corrections (and the undos
/// among them) were expected to change; every other month must be the same in both.
/// </summary>
public static class HistoryAudit
{
    private const string TooOld = "Ta korekta powstała w starszej wersji narzędzia i nie ma danych potrzebnych do kontroli miesięcy.";

    /// <summary>The state right after the chosen correction (the undos made before it was written are included).</summary>
    public static MonthlyAuditResult Run(string dataFolder, IReadOnlyList<CorrectionRecord> history, CorrectionRecord upTo, string? workFolder = null)
    {
        if (!HlcTimestamp.TryParse(upTo.LastTimestamp, out var boundary) || upTo.DeviceId.Length == 0) throw new AuditNotSupportedException(TooOld);
        return Run(dataFolder, history, boundary, upTo.Title, workFolder);
    }

    /// <summary>The state after everything the tool has written, the undos included.</summary>
    public static MonthlyAuditResult RunToTheEnd(string dataFolder, IReadOnlyList<CorrectionRecord> history, string? workFolder = null)
    {
        if (history.Count == 0) throw new AuditNotSupportedException("Nie ma jeszcze żadnej korekty.");
        if (history.Any(r => r.DeviceId.Length == 0 || !HlcTimestamp.TryParse(r.LastTimestamp, out _))) throw new AuditNotSupportedException(TooOld);
        var boundary = history.SelectMany(r => new[] { r.LastTimestamp, r.RevertTimestamp }).Where(t => HlcTimestamp.TryParse(t, out _)).Select(HlcTimestamp.Parse).Max();
        return Run(dataFolder, history, boundary, "cała historia korekt", workFolder);
    }

    private static MonthlyAuditResult Run(string dataFolder, IReadOnlyList<CorrectionRecord> history, HlcTimestamp boundary, string title, string? workFolder)
    {
        var toolDevices = history.Select(r => r.DeviceId).Where(d => d.Length > 0).Select(d => d.ToLowerInvariant()).ToHashSet();

        var events = ReadAllEvents(dataFolder);
        var baseline = events.Where(e => !toolDevices.Contains(e.DeviceId.ToLowerInvariant())).ToList();
        var after = events.Where(e => !toolDevices.Contains(e.DeviceId.ToLowerInvariant()) || e.Timestamp <= boundary).ToList();

        var work = Path.Combine(workFolder ?? Path.GetTempPath(), "budzet-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var before = Months(Replay(baseline, Path.Combine(work, "baseline.db")));
            var now = Months(Replay(after, Path.Combine(work, "after.db")));

            var expected = new Dictionary<DateOnly, DaySum>();
            void Expect(DateOnly day, DaySum change)
            {
                var month = new DateOnly(day.Year, day.Month, 1);
                expected[month] = expected.GetValueOrDefault(month, DaySum.Empty) + change;
            }

            int applies = 0, undos = 0;
            foreach (var record in history)
            {
                if (HlcTimestamp.TryParse(record.LastTimestamp, out var applied) && applied <= boundary)
                {
                    applies++;
                    foreach (var d in record.CheckedDays) Expect(d.Date, new DaySum(d.Expected.Income, d.Expected.Expense, d.Expected.Count));
                }
                if (record.Reverted && HlcTimestamp.TryParse(record.RevertTimestamp, out var undone) && undone <= boundary)
                {
                    undos++;
                    foreach (var d in record.RevertExpected) Expect(d.Date, new DaySum(d.Expected.Income, d.Expected.Expense, d.Expected.Count));
                }
            }

            var report = Verifier.Compare(before, now, expected.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value));
            return new MonthlyAuditResult(report, applies, undos, title);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static List<JournalEvent> ReadAllEvents(string dataFolder)
    {
        var events = new List<JournalEvent>();
        foreach (var path in Directory.GetFiles(dataFolder, JournalFiles.Prefix + "*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!JournalFiles.TryGetDeviceId(Path.GetFileName(path), out _)) continue;
            var read = JournalReader.ReadFrom(path, 0);
            if (read.UnsupportedSchemaVersion is not null) throw new InvalidOperationException("W folderze są dane nowszej wersji programu.");
            events.AddRange(read.Events);
        }
        return events;
    }

    /// <summary>Applies the events to an empty database the way the program does (the newest version of each entry wins) and returns its transactions.</summary>
    private static IReadOnlyList<Transaction> Replay(IReadOnlyList<JournalEvent> events, string databasePath)
    {
        var db = new LocalDatabase(databasePath);
        new Materializer(db).Apply(events);
        return new LocalReader(db).GetTransactions();
    }

    /// <summary>Totals per month, the month being named by its first day.</summary>
    private static Dictionary<DateOnly, DaySum> Months(IEnumerable<Transaction> transactions)
    {
        var months = new Dictionary<DateOnly, DaySum>();
        foreach (var t in transactions)
        {
            var month = new DateOnly(t.Date.Year, t.Date.Month, 1);
            months[month] = months.GetValueOrDefault(month, DaySum.Empty) + DaySum.Of(t);
        }
        return months;
    }
}
