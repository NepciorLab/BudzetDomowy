using BudzetDomowy.Core;
using BudzetDomowy.Core.Corrections;
using BudzetDomowy.Core.Domain;
using ClosedXML.Excel;

namespace BudzetDomowy.Tests;

/// <summary>The corrections: planning (change amounts, split, add every month), applying with a check day by day, and undoing.</summary>
public sealed class CorrectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-corr-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore(string name = "a")
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var core = BudgetCore.Create(Path.Combine(_root, "local-" + name), data, name, rateSource: new FakeRateSource());
        core.Categories.SeedIfEmpty();
        _cores.Add(core);
        return core;
    }

    private static DateOnly D(string s) => DateOnly.Parse(s);

    private static Transaction Tx(string date, decimal amount, TransactionType type = TransactionType.Expense, string currency = "PLN", decimal rate = 1m, string place = "Sklep") =>
        TransactionServiceCreate(D(date), type, amount, place, currency, rate);

    private static Transaction TransactionServiceCreate(DateOnly date, TransactionType type, decimal amount, string place, string currency, decimal rate) =>
        BudzetDomowy.Core.Services.TransactionService.Create(date, type, amount, null, "n", null, place, currency, rate, null, false, Guid.NewGuid(), Now, "dev");

    // ----- changing amounts -----

    [Fact]
    public void Shift_MakesEveryChosenAmountSmallerOrBiggerByTheSameSum()
    {
        var a = Tx("2026-01-01", 100m);
        var b = Tx("2026-01-02", 30m);

        var smaller = CorrectionPlanner.ShiftAmounts([a, b], -20m, "t");
        Assert.Equal([80m, 10m], smaller.Changes.Select(c => c.After.Amount));
        Assert.Equal([80m, 10m], smaller.Changes.Select(c => c.After.AmountPln));
        Assert.All(smaller.Changes, c => Assert.Equal((ChangeKind.Update, true), (c.Kind, c.Before is not null)));
        Assert.All(smaller.Changes, c => Assert.Equal(c.After.Amount, Assert.Single(c.After.Lines).Amount));       // the line follows

        var bigger = CorrectionPlanner.ShiftAmounts([a, b], 5.5m, "t");
        Assert.Equal([105.5m, 35.5m], bigger.Changes.Select(c => c.After.Amount));
    }

    [Fact]
    public void Shift_SkipsWhatWouldNotStayPositive_AndNothingForAZeroShift()
    {
        var a = Tx("2026-01-01", 100m);
        var b = Tx("2026-01-02", 30m);
        var c = Tx("2026-01-03", 20m);

        var plan = CorrectionPlanner.ShiftAmounts([a, b, c], -30m, "t");
        Assert.Equal([a.Id], plan.Changes.Select(x => x.After.Id));                          // 30 - 30 = 0 and 20 - 30 < 0 are left alone
        Assert.Equal([(b.Id, SkipReason.NotPositive), (c.Id, SkipReason.NotPositive)], plan.Skipped.Select(s => (s.Transaction.Id, s.Reason)));

        Assert.True(CorrectionPlanner.ShiftAmounts([a], 0m, "t").IsEmpty);
    }

    [Fact]
    public void Shift_OfAForeignEntry_ChangesItsOwnCurrency_AndZlotyFollowTheRate()
    {
        var eur = Tx("2026-01-01", 10m, currency: "EUR", rate: 4.25m);
        var change = Assert.Single(CorrectionPlanner.ShiftAmounts([eur], 2m, "t").Changes);
        Assert.Equal((12m, "EUR", 51m), (change.After.Amount, change.After.CurrencyCode, change.After.AmountPln));
    }

    [Fact]
    public void ThingsWithSeveralLines_AreNotTouched()
    {
        var multi = Tx("2026-01-01", 100m) with { Lines = [new TransactionLine { Id = Guid.NewGuid(), Amount = 60m }, new TransactionLine { Id = Guid.NewGuid(), Amount = 40m }] };
        Assert.Equal(SkipReason.SeveralLines, Assert.Single(CorrectionPlanner.ShiftAmounts([multi], -1m, "t").Skipped).Reason);
        Assert.Equal(SkipReason.SeveralLines, Assert.Single(CorrectionPlanner.Split([new SplitRequest(multi, 10m)], null, "dev", Now, "t").Skipped).Reason);
    }

    // ----- splitting -----

    [Fact]
    public void Split_GivesTheRestToTheOriginal_AndThePartToANewEntryInTheChosenCategory()
    {
        var core = NewCore();
        var food = core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;
        var sweets = core.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;
        var original = Tx("2026-03-05", 100m, place: "Lidl") with { CategoryId = food, Note = "zakupy" };

        var plan = CorrectionPlanner.Split([new SplitRequest(original, 30m)], sweets, "dev-x", Now, "t");

        var rest = plan.Changes.Single(c => c.Kind == ChangeKind.Update).After;
        var part = plan.Changes.Single(c => c.Kind == ChangeKind.Add).After;
        Assert.Equal((original.Id, 70m, food, D("2026-03-05")), (rest.Id, rest.Amount, rest.CategoryId, rest.Date));
        Assert.Equal((30m, 30m, sweets, D("2026-03-05")), (part.Amount, part.AmountPln, part.CategoryId, part.Date));
        Assert.Equal(("Lidl", "zakupy", TransactionType.Expense, "dev-x"), (part.Place, part.Note, part.Type, part.CreatedByDeviceId));
        Assert.NotEqual(original.Id, part.Id);
        Assert.Equal(30m, Assert.Single(part.Lines).Amount);
        Assert.Equal(sweets, Assert.Single(part.Lines).CategoryId);
        Assert.Equal(70m, Assert.Single(rest.Lines).Amount);

        // the totals of the day stay as they were: the split moves money between categories only
        Assert.Empty(plan.ExpectedDeltas().Where(kv => kv.Value.Income != 0 || kv.Value.Expense != 0));
        Assert.Equal(1, plan.ExpectedDeltas()[D("2026-03-05")].Count);
    }

    [Fact]
    public void Split_KeepsTheCurrencyOfAForeignEntry_AndSaysHonestlyWhatRoundingChanges()
    {
        var eur = Tx("2026-03-05", 10m, currency: "EUR", rate: 4.333m);                       // 43.33 zl
        var plan = CorrectionPlanner.Split([new SplitRequest(eur, 3m)], null, "dev", Now, "t");

        var rest = plan.Changes[0].After;
        var part = plan.Changes[1].After;
        Assert.Equal(("EUR", 7m, "EUR", 3m), (rest.CurrencyCode, rest.Amount, part.CurrencyCode, part.Amount));
        Assert.Equal((4.333m, 4.333m), (rest.ExchangeRateToPln, part.ExchangeRateToPln));
        // 7 * 4.333 = 30.331 -> 30.33, 3 * 4.333 = 12.999 -> 13.00: together 43.33, as the whole was; the plan states the exact sums either way
        var delta = plan.ExpectedDeltas()[D("2026-03-05")];
        Assert.Equal(rest.AmountPln + part.AmountPln - eur.AmountPln, delta.Expense);
    }

    [Theory]
    [InlineData(0, SkipReason.NoPart)]
    [InlineData(-5, SkipReason.NoPart)]
    [InlineData(100, SkipReason.PartTooBig)]
    [InlineData(150, SkipReason.PartTooBig)]
    public void Split_RefusesAPartThatIsNotInsideTheAmount(int part, SkipReason reason)
    {
        var plan = CorrectionPlanner.Split([new SplitRequest(Tx("2026-03-05", 100m), part)], null, "dev", Now, "t");
        Assert.True(plan.IsEmpty);
        Assert.Equal(reason, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Split_EachEntryHasItsOwnPart()
    {
        var a = Tx("2026-03-05", 100m);
        var b = Tx("2026-03-06", 50m, TransactionType.Income);
        var plan = CorrectionPlanner.Split([new SplitRequest(a, 10m), new SplitRequest(b, 25m)], null, "dev", Now, "t");
        Assert.Equal(4, plan.Changes.Count);
        Assert.Equal([10m, 25m], plan.Changes.Where(c => c.Kind == ChangeKind.Add).Select(c => c.After.Amount));
        Assert.Equal([90m, 25m], plan.Changes.Where(c => c.Kind == ChangeKind.Update).Select(c => c.After.Amount));
        Assert.Equal(TransactionType.Income, plan.Changes.Single(c => c.Kind == ChangeKind.Add && c.After.Amount == 25m).After.Type);
    }

    // ----- adding every month -----

    [Fact]
    public void MonthlyDates_FallOnTheGivenDay_OrTheLastOneOfAShorterMonth()
    {
        Assert.Equal([D("2026-01-31"), D("2026-02-28"), D("2026-03-31"), D("2026-04-30")], CorrectionPlanner.MonthlyDates(D("2026-01-01"), D("2026-04-30"), 31));
        Assert.Equal([D("2024-02-29")], CorrectionPlanner.MonthlyDates(D("2024-02-01"), D("2024-02-29"), 31));         // a leap year
        Assert.Equal([D("2026-02-10"), D("2026-03-10")], CorrectionPlanner.MonthlyDates(D("2026-01-15"), D("2026-03-31"), 10));   // January's 10th is before the range
        Assert.Equal([D("2026-01-15")], CorrectionPlanner.MonthlyDates(D("2026-01-15"), D("2026-01-15"), 15));
        Assert.Empty(CorrectionPlanner.MonthlyDates(D("2026-03-01"), D("2026-02-01"), 5));                             // reversed
        Assert.Empty(CorrectionPlanner.MonthlyDates(D("2026-01-01"), D("2026-03-01"), 0));
        Assert.Empty(CorrectionPlanner.MonthlyDates(D("2026-01-01"), D("2026-03-01"), 32));
        Assert.Equal(180, CorrectionPlanner.MonthlyDates(D("2011-01-01"), D("2025-12-31"), 5).Count);                  // fifteen years
    }

    [Fact]
    public void AddMonthly_CreatesAnEntryForEachMonth_ExpensesAndIncomesAlike()
    {
        var spec = new MonthlyAddSpec(TransactionType.Income, 250m, null, "Dodatek", "co miesiąc", D("2026-01-01"), D("2026-06-30"), 10);
        var plan = CorrectionPlanner.AddMonthly(spec, "dev", Now, "t");

        Assert.Equal(6, plan.Adds);
        Assert.All(plan.Changes, c => Assert.Equal((ChangeKind.Add, null, TransactionType.Income, 250m, "Dodatek"), (c.Kind, c.Before, c.After.Type, c.After.AmountPln, c.After.Place)));
        Assert.Equal(6, plan.Changes.Select(c => c.After.Id).Distinct().Count());
        var deltas = plan.ExpectedDeltas();
        Assert.Equal(6, deltas.Count);
        Assert.All(deltas.Values, d => Assert.Equal(new DaySum(250m, 0m, 1), d));

        var expense = CorrectionPlanner.AddMonthly(spec with { Type = TransactionType.Expense }, "dev", Now, "t");
        Assert.All(expense.ExpectedDeltas().Values, d => Assert.Equal(new DaySum(0m, 250m, 1), d));

        Assert.Throws<ArgumentOutOfRangeException>(() => CorrectionPlanner.AddMonthly(spec with { Amount = 0 }, "dev", Now, "t"));
        Assert.Throws<ArgumentException>(() => CorrectionPlanner.AddMonthly(spec with { From = D("1900-01-01"), To = D("2026-12-31") }, "dev", Now, "t"));
    }

    // ----- applying, checking, undoing -----

    private static void Seed(BudgetCore core)
    {
        for (var i = 0; i < 6; i++)
            core.Transactions.Add(D("2026-02-01").AddDays(i * 3), TransactionType.Expense, 100m + i, null, "n" + i, null, place: "P" + i);
        core.Transactions.Add(D("2026-02-02"), TransactionType.Income, 5000m, null, "pensja", null, place: "Firma");
    }

    [Fact]
    public void Apply_WritesThePlan_ChecksEveryDay_AndKeepsARecord()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var chosen = core.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Take(3).ToList();
        var plan = CorrectionPlanner.ShiftAmounts(chosen, -10m, "Zmniejszenie o 10");

        var result = runner.Apply(plan);

        Assert.True(result.Report.AllOk);
        Assert.Empty(result.Report.Problems);
        Assert.Equal(3, result.Report.ChangedDays);
        Assert.Equal(new DaySum(0m, -30m, 0), result.Report.TotalAfter - result.Report.TotalBefore);
        var now = core.Reader.GetTransactions().ToDictionary(t => t.Id);
        Assert.All(chosen, t => Assert.Equal(t.Amount - 10m, now[t.Id].Amount));
        Assert.Equal(7, now.Count);

        var record = Assert.Single(runner.History());
        Assert.Equal(("Zmniejszenie o 10", true, false, 3), (record.Title, record.AllOk, record.Reverted, record.Changes.Count));
        Assert.True(File.Exists(Path.Combine(runner.Folder, record.FileName)));
        Assert.NotEqual("", record.BackupFile);                                              // a backup was made before writing
        Assert.Contains(core.Backups.List(), b => b.FileName == record.BackupFile);
        Assert.All(record.Changes, c => Assert.NotNull(c.Before));                           // the old versions are kept for the undo
    }

    [Fact]
    public void Apply_OfASplit_KeepsEveryDaysTotals_AndAddsTheEntries()
    {
        var core = NewCore();
        Seed(core);
        var food = core.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;
        var runner = new CorrectionRunner(core, () => Now);
        var requests = core.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Take(4).Select(t => new SplitRequest(t, 10m + (t.Amount - 100m))).ToList();
        var daysBefore = DaySnapshot.Build(core.Reader.GetTransactions());

        var result = runner.Apply(CorrectionPlanner.Split(requests, food, core.Device.DeviceId, Now, "Rozbicie"));

        Assert.True(result.Report.AllOk);
        var after = DaySnapshot.Build(core.Reader.GetTransactions());
        Assert.All(daysBefore, kv => Assert.Equal(kv.Value with { Count = kv.Value.Count + (requests.Any(r => r.Original.Date == kv.Key) ? 1 : 0) }, after[kv.Key]));
        Assert.Equal(7 + 4, core.Reader.GetTransactions().Count);
        Assert.Equal(4, core.Reader.GetTransactions().Count(t => t.CategoryId == food));
    }

    [Fact]
    public void TheCheckFindsADayThatDidNotChangeAsPromised()
    {
        var before = new Dictionary<DateOnly, DaySum> { [D("2026-01-01")] = new(0m, 100m, 1), [D("2026-01-02")] = new(0m, 50m, 1), [D("2026-01-03")] = new(10m, 0m, 1) };
        var expected = new Dictionary<DateOnly, DaySum> { [D("2026-01-01")] = new(0m, -10m, 0) };
        var after = new Dictionary<DateOnly, DaySum>
        {
            [D("2026-01-01")] = new(0m, 90m, 1),                  // as promised
            [D("2026-01-02")] = new(0m, 49m, 1),                  // a day nobody should have touched
            [D("2026-01-03")] = new(10m, 0m, 1),
        };

        var report = Verifier.Compare(before, after, expected);

        Assert.False(report.AllOk);
        var problem = Assert.Single(report.Problems);
        Assert.Equal((D("2026-01-02"), new DaySum(0m, -1m, 0), DaySum.Empty), (problem.Date, problem.Actual, problem.Expected));
        Assert.Equal(3, report.DaysChecked);
        Assert.Equal(2, report.ChangedDays);                                                // only the days with a promise or a change are listed

        var good = Verifier.Compare(before, new Dictionary<DateOnly, DaySum>(before) { [D("2026-01-01")] = new(0m, 90m, 1) }, expected);
        Assert.True(good.AllOk);
        Assert.Equal(1, good.ChangedDays);
    }

    [Fact]
    public void Apply_RefusesWhenTheDataChangedSincePlanning_AndWritesNothing()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var chosen = core.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Take(2).ToList();
        var plan = CorrectionPlanner.ShiftAmounts(chosen, -1m, "t");

        core.Transactions.Update(chosen[1] with { Note = "ktoś zmienił" });                    // another computer, or the main program

        Assert.Throws<CorrectionConflictException>(() => runner.Apply(plan));
        Assert.Equal([100m, 101m], core.Reader.GetTransactions().Where(t => chosen.Select(c => c.Id).Contains(t.Id)).Select(t => t.Amount).OrderBy(a => a));
        Assert.Empty(runner.History());
    }

    [Fact]
    public void Apply_TwiceWithTheSamePlan_IsRefused()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var plan = CorrectionPlanner.AddMonthly(new MonthlyAddSpec(TransactionType.Expense, 20m, null, "X", "", D("2026-03-01"), D("2026-05-31"), 1), "dev", Now, "t");

        runner.Apply(plan);
        Assert.Throws<CorrectionConflictException>(() => runner.Apply(plan));
        Assert.Equal(7 + 3, core.Reader.GetTransactions().Count);
    }

    [Fact]
    public void Revert_PutsEverythingBack_AndTheCheckConfirmsIt()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var original = core.Reader.GetTransactions().OrderBy(t => t.Id).ToList();
        var daysBefore = DaySnapshot.Build(original);
        var food = core.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;

        var split = runner.Apply(CorrectionPlanner.Split(original.Where(t => t.Type == TransactionType.Expense).Take(2).Select(t => new SplitRequest(t, 20m)).ToList(), food, "dev", Now, "split"));
        Assert.Equal(9, core.Reader.GetTransactions().Count);

        var report = runner.Revert(split.Record);

        Assert.True(report.AllOk);
        Assert.Equal(7, core.Reader.GetTransactions().Count);
        Assert.Equal(daysBefore, DaySnapshot.Build(core.Reader.GetTransactions()));
        var back = core.Reader.GetTransactions().OrderBy(t => t.Id).ToList();
        Assert.Equal(original.Select(t => (t.Id, t.Amount, t.AmountPln, t.CategoryId, t.Note)), back.Select(t => (t.Id, t.Amount, t.AmountPln, t.CategoryId, t.Note)));
        Assert.True(Assert.Single(runner.History()).Reverted);
        Assert.Throws<InvalidOperationException>(() => runner.Revert(split.Record));
    }

    [Fact]
    public void Revert_CopesWithOtherChangesMadeInTheMeantime()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var applied = runner.Apply(CorrectionPlanner.AddMonthly(new MonthlyAddSpec(TransactionType.Income, 40m, null, "Dod", "", D("2026-03-01"), D("2026-04-30"), 5), "dev", Now, "t"));

        core.Transactions.Add(D("2026-03-20"), TransactionType.Expense, 12m, null, "po korekcie", null);               // something new, unrelated
        var added = applied.Record.Changes[0].TransactionId;
        core.Transactions.Update(core.Reader.GetTransactions().Single(t => t.Id == added) with { Note = "poprawiona ręcznie" });

        var report = runner.Revert(applied.Record);

        Assert.True(report.AllOk);                                                           // the expectation is built from the entries as they are now
        Assert.DoesNotContain(core.Reader.GetTransactions(), t => t.Place == "Dod");
        Assert.Contains(core.Reader.GetTransactions(), t => t.Note == "po korekcie");
    }

    [Fact]
    public void ACorrectionReachesTheOtherComputer_AndSoDoesItsUndo()
    {
        var a = NewCore("a");
        Seed(a);
        var b = NewCore("b");
        b.Sync.SyncOnce();
        Assert.Equal(7, b.Reader.GetTransactions().Count);

        var runner = new CorrectionRunner(a, () => Now);
        var chosen = a.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).ToList();
        var result = runner.Apply(CorrectionPlanner.ShiftAmounts(chosen, 1m, "plus 1"));
        b.Sync.SyncOnce();
        Assert.Equal(chosen.Select(t => t.Amount + 1m).OrderBy(x => x), b.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Select(t => t.Amount).OrderBy(x => x));

        runner.Revert(result.Record);
        b.Sync.SyncOnce();                                                                    // no rebuild needed: the undo is made of ordinary events
        Assert.Equal(chosen.Select(t => t.Amount).OrderBy(x => x), b.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Select(t => t.Amount).OrderBy(x => x));
    }

    [Fact]
    public void TheReportCanBeExportedToExcel_AndRebuiltFromTheRecord()
    {
        var core = NewCore();
        Seed(core);
        var runner = new CorrectionRunner(core, () => Now);
        var result = runner.Apply(CorrectionPlanner.ShiftAmounts(core.Reader.GetTransactions().Where(t => t.Type == TransactionType.Expense).Take(2).ToList(), -5m, "Mniej o 5"));

        var fromRecord = CorrectionRunner.ReportOf(runner.History()[0]);
        Assert.True(fromRecord.AllOk);
        Assert.Equal(result.Report.ChangedDays, fromRecord.ChangedDays);
        Assert.Equal(result.Report.Days.Select(d => (d.Date, d.Expected)), fromRecord.Days.Select(d => (d.Date, d.Expected)));

        var path = Path.Combine(_root, "kontrola.xlsx");
        VerificationExport.Write(path, "Mniej o 5", result.Report);
        using var wb = new XLWorkbook(path);
        Assert.Equal(["Podsumowanie", "Dni"], wb.Worksheets.Select(w => w.Name));
        Assert.Contains("wszystko się zgadza", wb.Worksheet("Podsumowanie").Cell(2, 1).GetString());
        var days = wb.Worksheet("Dni");
        Assert.Equal(1 + result.Report.ChangedDays, days.RangeUsed()!.RowCount());
        Assert.Equal("OK", days.Cell(2, 12).GetString());
    }
}
