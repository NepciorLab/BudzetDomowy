using BudzetDomowy.Core;
using BudzetDomowy.Core.Corrections;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using ClosedXML.Excel;

namespace BudzetDomowy.Tests;

/// <summary>The month-by-month check of the whole history of corrections: before any correction at all against after the chosen one.</summary>
public sealed class HistoryAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-audit-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Data => Path.Combine(_root, "data");

    private BudgetCore NewCore(string name)
    {
        Directory.CreateDirectory(Data);
        var core = BudgetCore.Create(Path.Combine(_root, "local-" + name), Data, name, rateSource: new FakeRateSource());
        _cores.Add(core);
        return core;
    }

    private static DateOnly D(string s) => DateOnly.Parse(s);

    /// <summary>Entries in January, February and March of 2026: two expenses and an income in each month.</summary>
    private BudgetCore Seed()
    {
        var core = NewCore("main");
        core.Categories.SeedIfEmpty();
        foreach (var month in new[] { 1, 2, 3 })
        {
            core.Transactions.Add(new DateOnly(2026, month, 3), TransactionType.Expense, 100m, null, $"a{month}", null, place: "Sklep");
            core.Transactions.Add(new DateOnly(2026, month, 20), TransactionType.Expense, 50m, null, $"b{month}", null, place: "Kiosk");
            core.Transactions.Add(new DateOnly(2026, month, 10), TransactionType.Income, 1000m, null, $"p{month}", null, place: "Firma");
        }
        return core;
    }

    private static CorrectionRunner Tool(BudgetCore core) => new(core, () => Now);

    private MonthlyAuditResult Audit(CorrectionRunner runner, CorrectionRecord upTo) => HistoryAudit.Run(Data, runner.History(), upTo);

    [Fact]
    public void ANeutralCorrection_LeavesEveryMonthExactlyAsItWas()
    {
        var main = Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var food = main.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;
        var applied = runner.Apply(CorrectionPlanner.Split(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").Select(t => new SplitRequest(t, 30m)).ToList(), food, tool.Device.DeviceId, Now, "rozbicie"));

        var audit = Audit(runner, applied.Record);

        Assert.True(audit.Report.AllOk);
        Assert.Equal(3, audit.Report.DaysChecked);                                           // three months of data
        Assert.Empty(audit.Report.Differences);                                              // the sums of none of them differ
        Assert.Equal(3, audit.Report.ChangedDays);                                           // (each month only has one more entry: that was promised)
        Assert.Equal(DaySum.Empty with { Count = 3 }, audit.Report.TotalChange);             // the sums are the same, only the entries are three more
        Assert.Equal(DaySum.Empty, audit.Report.TotalUnexpected);
        Assert.Equal(3, audit.Report.All.Count);                                             // all three months are listed, unchanged
        Assert.Equal((1, 0), (audit.CorrectionsIncluded, audit.UndosIncluded));
    }

    [Fact]
    public void TheCorrectionsUpToTheChosenOne_ChangeExactlyWhatTheyPromised()
    {
        Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();

        runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep" && t.Date.Month <= 2).ToList(), -10m, "pierwsza"));
        runner.Apply(CorrectionPlanner.AddMonthly(new MonthlyAddSpec(TransactionType.Income, 40m, null, "Dodatek", "", D("2026-02-01"), D("2026-03-31"), 15), tool.Device.DeviceId, Now, "druga"));
        runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Kiosk" && t.Date.Month == 3).ToList(), 5m, "trzecia"));

        var upToFirst = Audit(runner, runner.History().Single(r => r.Title == "pierwsza"));
        Assert.True(upToFirst.Report.AllOk);
        Assert.Equal(2, upToFirst.Report.ChangedDays);                                       // January and February
        Assert.Equal(new DaySum(0m, -20m, 0), upToFirst.Report.TotalChange);
        Assert.Equal([D("2026-01-01"), D("2026-02-01")], upToFirst.Report.Differences.Select(d => d.Date));
        Assert.Equal(1, upToFirst.CorrectionsIncluded);

        var upToSecond = Audit(runner, runner.History().Single(r => r.Title == "druga"));
        Assert.True(upToSecond.Report.AllOk);
        Assert.Equal(new DaySum(80m, -20m, 2), upToSecond.Report.TotalChange);
        Assert.Equal(2, upToSecond.CorrectionsIncluded);
        Assert.Equal(new DaySum(40m, 0m, 1), upToSecond.Report.Days.Single(d => d.Date == D("2026-03-01")).Actual);          // the added entry only

        var upToThird = Audit(runner, runner.History().Single(r => r.Title == "trzecia"));
        Assert.True(upToThird.Report.AllOk);
        Assert.Equal(new DaySum(80m, -15m, 2), upToThird.Report.TotalChange);
        Assert.Equal(3, upToThird.CorrectionsIncluded);
        Assert.Equal(new DaySum(3000m, 450m, 9), upToThird.Report.TotalBefore);              // "before any correction at all": the same in every check
        Assert.Equal(upToFirst.Report.TotalBefore, upToThird.Report.TotalBefore);
    }

    [Fact]
    public void AnUndoneCorrectionIsTakenIntoAccount_OnlyForTheChecksAfterTheUndo()
    {
        Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -10m, "A"));
        runner.Apply(CorrectionPlanner.AddMonthly(new MonthlyAddSpec(TransactionType.Expense, 7m, null, "X", "", D("2026-01-01"), D("2026-03-31"), 5), tool.Device.DeviceId, Now, "B"));
        runner.Revert(runner.History().Single(r => r.Title == "A"));

        var history = runner.History();
        var upToB = HistoryAudit.Run(Data, history, history.Single(r => r.Title == "B"));          // the undo of A happened after B was written
        Assert.True(upToB.Report.AllOk);
        Assert.Equal((2, 0), (upToB.CorrectionsIncluded, upToB.UndosIncluded));
        Assert.Equal(new DaySum(0m, -30m + 21m, 3), upToB.Report.TotalChange);

        var everything = HistoryAudit.RunToTheEnd(Data, history);
        Assert.True(everything.Report.AllOk);
        Assert.Equal((2, 1), (everything.CorrectionsIncluded, everything.UndosIncluded));
        Assert.Equal(new DaySum(0m, 21m, 3), everything.Report.TotalChange);                 // only B is left
    }

    [Fact]
    public void WhatTheUserDoesLater_IsInBothStatesAndIsNotAMistake()
    {
        var main = Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var applied = runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -10m, "A"));

        main.Transactions.Add(D("2026-02-14"), TransactionType.Expense, 33m, null, "po korekcie", null, place: "Nowy");          // typed in the main program afterwards
        main.Transactions.Add(D("2026-04-01"), TransactionType.Expense, 12m, null, "nowy miesiąc", null, place: "Nowy");

        var audit = Audit(runner, applied.Record);

        Assert.True(audit.Report.AllOk);
        Assert.Equal(4, audit.Report.DaysChecked);                                           // the month of the new entry counts too
        Assert.Equal(3, audit.Report.ChangedDays);                                           // January, February, March: the correction; April is the same
    }

    [Fact]
    public void AnEntryEditedLaterByTheUser_ShowsAsADifferenceInItsMonth_WithTheSumsOfTheDifferences()
    {
        var main = Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var applied = runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -10m, "A"));

        main.Sync.SyncOnce();
        var march = main.Reader.GetTransactions().Single(t => t.Place == "Sklep" && t.Date.Month == 3);
        main.Transactions.Update(march with { Amount = 100m });                              // the user puts the old amount back by hand

        var audit = Audit(runner, applied.Record);

        Assert.False(audit.Report.AllOk);
        var problem = Assert.Single(audit.Report.Problems);
        Assert.Equal(D("2026-03-01"), problem.Date);
        Assert.Equal(DaySum.Empty, problem.Actual);                                          // nothing changed in March ...
        Assert.Equal(new DaySum(0m, -10m, 0), problem.Expected);                             // ... though the correction promised -10
        Assert.Equal(new DaySum(0m, 10m, 0), audit.Report.TotalUnexpected);                  // against the promise: 10 zl more spent in March
        Assert.Equal(new DaySum(0m, -20m, 0), audit.Report.TotalChange);                     // what really changed: January and February
        Assert.Equal([D("2026-01-01"), D("2026-02-01")], audit.Report.Differences.Select(d => d.Date));
    }

    [Fact]
    public void SeveralInstallationsOfTheTool_AreAllToldApartFromTheRealData()
    {
        Seed();
        var one = NewCore("korekta-1");
        var two = NewCore("korekta-2");
        var first = Tool(one);
        var second = Tool(two);
        one.Sync.SyncOnce();
        first.Apply(CorrectionPlanner.ShiftAmounts(one.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -10m, "z jednego"));
        two.Sync.SyncOnce();
        second.Apply(CorrectionPlanner.ShiftAmounts(two.Reader.GetTransactions().Where(t => t.Place == "Kiosk").ToList(), -5m, "z drugiego"));

        var history = second.History();
        Assert.Equal(2, history.Select(r => r.DeviceId).Distinct().Count());
        var audit = HistoryAudit.RunToTheEnd(Data, history);

        Assert.True(audit.Report.AllOk);
        Assert.Equal(new DaySum(0m, -45m, 0), audit.Report.TotalChange);
        Assert.Equal(new DaySum(3000m, 450m, 9), audit.Report.TotalBefore);
    }

    [Fact]
    public void ARecordFromAnOlderVersionOfTheTool_CannotBeChecked_AndSaysSo()
    {
        Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var applied = runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -1m, "stara"));

        var old = new CorrectionRecord { Id = applied.Record.Id, AppliedAt = applied.Record.AppliedAt, Title = "stara" };           // no device and no time stamp
        Assert.Throws<AuditNotSupportedException>(() => HistoryAudit.Run(Data, [old], old));
        Assert.Throws<AuditNotSupportedException>(() => HistoryAudit.RunToTheEnd(Data, [old]));
        Assert.Throws<AuditNotSupportedException>(() => HistoryAudit.RunToTheEnd(Data, []));
    }

    [Fact]
    public void TheMonthlyReportGoesToExcelWithEveryMonth()
    {
        Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var applied = runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep" && t.Date.Month == 1).ToList(), -10m, "A"));
        var audit = Audit(runner, applied.Record);

        var path = Path.Combine(_root, "miesiace.xlsx");
        VerificationExport.Write(path, "A – miesiące", audit.Report, monthly: true);

        using var wb = new XLWorkbook(path);
        Assert.Equal(["Podsumowanie", "Miesiące"], wb.Worksheets.Select(w => w.Name));
        Assert.Contains("Sprawdzonych miesięcy", wb.Worksheet("Podsumowanie").Cell(3, 1).GetString());
        var sheet = wb.Worksheet("Miesiące");
        Assert.Equal("Miesiąc", sheet.Cell(1, 1).GetString());
        Assert.Equal(1 + 3, sheet.RangeUsed()!.RowCount());                                  // January changed, February and March are listed as they were
        Assert.Equal("OK", sheet.Cell(2, 12).GetString());
    }

    [Fact]
    public void TheRecordsKeepWhatTheAuditNeeds_AfterBeingSavedAndRead()
    {
        Seed();
        var tool = NewCore("korekta");
        var runner = Tool(tool);
        tool.Sync.SyncOnce();
        var applied = runner.Apply(CorrectionPlanner.ShiftAmounts(tool.Reader.GetTransactions().Where(t => t.Place == "Sklep").ToList(), -1m, "A"));
        runner.Revert(applied.Record);

        var record = Assert.Single(runner.History());
        Assert.Equal(tool.Device.DeviceId.ToLowerInvariant(), record.DeviceId);
        Assert.True(HlcTimestamp.TryParse(record.LastTimestamp, out var applyTime));
        Assert.True(HlcTimestamp.TryParse(record.RevertTimestamp, out var undoTime));
        Assert.True(undoTime > applyTime);
        Assert.NotEmpty(record.RevertExpected);
    }
}
