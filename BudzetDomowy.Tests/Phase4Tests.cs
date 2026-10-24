using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

internal static class Make
{
    public static Transaction Tx(string date, TransactionType type, decimal pln, Guid? category = null, string currency = "PLN") => new()
    {
        Id = Guid.NewGuid(),
        Date = DateOnly.Parse(date),
        Type = type,
        Amount = pln,
        AmountPln = pln,
        CurrencyCode = currency,
        CategoryId = category,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public static Transaction Expense(string date, decimal pln, Guid? category = null) => Tx(date, TransactionType.Expense, pln, category);
    public static Transaction Income(string date, decimal pln) => Tx(date, TransactionType.Income, pln);
}

public class BudgetMathTests
{
    private static Budget B(Guid cat, string month, decimal amount) => new()
    {
        Id = Guid.NewGuid(), CategoryId = cat, MonthlyAmount = amount, ValidFromMonth = DateOnly.Parse(month),
    };

    [Fact]
    public void Effective_UsesTheNewestEntryStartedByThatMonth_AndZeroSwitchesTheLimitOff()
    {
        var a = Guid.NewGuid();
        var budgets = new[] { B(a, "2026-01-01", 500), B(a, "2026-03-01", 700), B(a, "2026-06-01", 0) };

        Assert.Empty(BudgetMath.Effective(budgets, new DateOnly(2025, 12, 15)));
        Assert.Equal(500m, BudgetMath.Effective(budgets, new DateOnly(2026, 2, 28))[a]);
        Assert.Equal(700m, BudgetMath.Effective(budgets, new DateOnly(2026, 3, 1))[a]);
        Assert.Equal(700m, BudgetMath.Effective(budgets, new DateOnly(2026, 5, 31))[a]);
        Assert.Empty(BudgetMath.Effective(budgets, new DateOnly(2026, 6, 10)));
        Assert.Empty(BudgetMath.Effective(budgets, new DateOnly(2026, 12, 1)));
    }

    [Theory]
    [InlineData(0.0, BudgetStatus.Ok)]
    [InlineData(0.79, BudgetStatus.Ok)]
    [InlineData(0.80, BudgetStatus.Warning)]
    [InlineData(1.00, BudgetStatus.Warning)]
    [InlineData(1.01, BudgetStatus.Over)]
    public void Status_WarnsFromEightyPercent_AndTurnsRedOnlyAfterExceeding(double fraction, BudgetStatus expected) =>
        Assert.Equal(expected, BudgetMath.StatusOf((decimal)fraction));

    [Fact]
    public void Progress_CountsSubcategoriesInTheParentLimit_AndIgnoresOtherMonthsAndIncome()
    {
        var cats = DefaultCategories.Create();
        var food = cats.First(c => c.Name == "Spożywcze");
        var bakery = cats.First(c => c.Name == "Piekarnia");
        var budgets = new[]
        {
            new Budget { Id = Guid.NewGuid(), CategoryId = food.Id, MonthlyAmount = 1000, ValidFromMonth = new DateOnly(2026, 1, 1) },
            new Budget { Id = Guid.NewGuid(), CategoryId = bakery.Id, MonthlyAmount = 150, ValidFromMonth = new DateOnly(2026, 1, 1) },
        };
        var tx = new[]
        {
            Make.Expense("2026-10-03", 700, food.Id),
            Make.Expense("2026-10-09", 200, bakery.Id),
            Make.Expense("2026-09-30", 999, food.Id),     // other month
            Make.Expense("2026-11-01", 999, food.Id),     // other month
            Make.Income("2026-10-05", 5000),              // income never counts
            Make.Tx("2026-10-12", TransactionType.Expense, 50, null),
        };

        var lines = BudgetMath.Progress(new DateOnly(2026, 10, 17), budgets, cats, tx);

        Assert.Equal(2, lines.Count);
        var child = lines[0]; // sorted by how much of the limit is used
        Assert.Equal("Spożywcze › Piekarnia", child.Path);
        Assert.Equal(200m, child.Spent);
        Assert.Equal(BudgetStatus.Over, child.Status);
        Assert.Equal(-50m, child.Remaining);

        var parent = lines[1];
        Assert.Equal(900m, parent.Spent);
        Assert.Equal(0.9m, parent.Fraction);
        Assert.Equal(BudgetStatus.Warning, parent.Status);
    }
}

public class PeriodReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 24);

    [Fact]
    public void MonthAndYearSummaries_AndComparisonWithThePreviousPeriod()
    {
        var tx = new[]
        {
            Make.Expense("2026-09-10", 100), Make.Income("2026-09-01", 500),
            Make.Expense("2026-10-02", 100), Make.Expense("2026-10-31", 50), Make.Income("2026-10-01", 500),
            Make.Expense("2025-10-05", 400),
        };
        var oct = PeriodReports.MonthSummary(tx, new DateOnly(2026, 10, 15));
        Assert.Equal((150m, 500m, 350m, 3), (oct.Expense, oct.Income, oct.Balance, oct.Count));

        var cmp = PeriodReports.CompareMonth(tx, new DateOnly(2026, 10, 1));
        Assert.Equal(50m, cmp.Expense.Delta);
        Assert.Equal(0.5m, cmp.Expense.Percent);
        Assert.Equal(0m, cmp.Income.Delta);
        Assert.Equal(50m, cmp.Balance.Delta * -1);

        var year = PeriodReports.CompareYear(tx, 2026);
        Assert.Equal(250m, year.Current.Expense);
        Assert.Equal(400m, year.Previous.Expense);
        Assert.Equal(-0.375m, year.Expense.Percent);
        Assert.Null(PeriodReports.CompareYear(tx, 2025).Expense.Percent); // nothing in 2024
    }

    [Fact]
    public void MonthlyTrend_ListsEveryMonth_EvenWithoutData()
    {
        var tx = new[] { Make.Expense("2026-01-15", 10), Make.Income("2026-03-02", 99), Make.Expense("2026-03-20", 5), Make.Expense("2027-01-01", 1000) };
        var trend = PeriodReports.MonthlyTrend(tx, new DateOnly(2026, 1, 20), new DateOnly(2026, 3, 1));
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)], trend.Select(p => p.Month));
        Assert.Equal([10m, 0m, 5m], trend.Select(p => p.Expense));
        Assert.Equal([0m, 0m, 99m], trend.Select(p => p.Income));
        Assert.Equal(94m, trend[2].Balance);
    }

    [Fact]
    public void Breakdown_RollsSubcategoriesUp_AndChildrenAddUpToTheParent()
    {
        var cats = DefaultCategories.Create();
        Guid Id(string n) => cats.First(c => c.Name == n).Id;
        var tx = new[]
        {
            Make.Expense("2026-10-01", 10, Id("Auto")),
            Make.Expense("2026-10-02", 30, Id("Paliwo")),
            Make.Expense("2026-10-03", 20, Id("Serwis i naprawy")),
            Make.Expense("2026-10-04", 40, Id("Spożywcze")),
            Make.Tx("2026-10-05", TransactionType.Expense, 5, null),
            Make.Expense("2026-10-06", 999, Id("Chemia")).With(x => x with { Type = TransactionType.Income }), // other type
            Make.Expense("2026-11-01", 999, Id("Chemia")),                                                        // outside the range
        };

        var roots = PeriodReports.Breakdown(tx, cats, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TransactionType.Expense);

        Assert.Equal(["Auto", "Spożywcze", ""], roots.Select(r => r.Name));
        Assert.Equal([60m, 40m, 5m], roots.Select(r => r.Amount));
        Assert.Equal(60m / 105m, roots[0].Share);

        var auto = roots[0];
        Assert.Equal(["Paliwo", "Serwis i naprawy", "Auto"], auto.Children.Select(c => c.Name));
        Assert.Equal(auto.Amount, auto.Children.Sum(c => c.Amount));
        Assert.True(auto.Children[2].IsOwn);
        Assert.Equal(0.5m, auto.Children[0].Share);
        Assert.Null(roots[2].CategoryId);

        Assert.Equal(["Auto", "Spożywcze"], PeriodReports.TopCategories(roots, 2).Select(r => r.Name));
    }

    private static readonly Transaction[] SepOct =
    [
        Make.Expense("2026-09-15", 300), Make.Expense("2026-10-10", 240), Make.Income("2026-09-01", 1000),
    ];

    [Fact]
    public void Average_ProratesTheCurrentMonth()
    {
        var a = PeriodReports.Average(SepOct, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), Today, IncompleteMonthMode.Prorate);

        Assert.Equal(54, a.Days);                         // Sep 1 .. Oct 24
        Assert.Equal(540m, a.Expense);
        Assert.Equal(10m, a.ExpensePerDay);
        Assert.Equal(70m, a.ExpensePerWeek);
        Assert.Equal(1.7742m, a.Months);                  // 1 + 24/31
        Assert.Equal(304.36m, a.ExpensePerMonth);         // 540 * 31 / 55
        Assert.Equal(563.64m, a.IncomePerMonth);          // 1000 * 31 / 55
    }

    [Fact]
    public void Average_CanLeaveOutTheIncompleteMonth()
    {
        var a = PeriodReports.Average(SepOct, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), Today, IncompleteMonthMode.ExcludeIncomplete);

        Assert.Equal(30, a.Days);                         // September only
        Assert.Equal(300m, a.Expense);
        Assert.Equal(1m, a.Months);
        Assert.Equal(300m, a.ExpensePerMonth);
        Assert.Equal(10m, a.ExpensePerDay);
    }

    [Fact]
    public void Average_NeverCountsDaysThatHaveNotHappened()
    {
        var a = PeriodReports.Average(SepOct, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 10, 10), IncompleteMonthMode.Prorate);
        Assert.Equal(10, a.Days);
        Assert.Equal(24m, a.ExpensePerDay);

        var future = PeriodReports.Average(SepOct, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Today, IncompleteMonthMode.Prorate);
        Assert.Equal(0, future.Days);
        Assert.Null(future.ExpensePerDay);
        Assert.Null(future.ExpensePerMonth);
    }

    [Fact]
    public void Average_ExcludeMode_WithoutAnyWholeMonth_StillGivesDailyAndWeeklyFigures()
    {
        var a = PeriodReports.Average(SepOct, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 24), Today, IncompleteMonthMode.ExcludeIncomplete);
        Assert.Equal(0m, a.Months);
        Assert.Null(a.ExpensePerMonth);
        Assert.Equal(20, a.Days);
        Assert.Equal(12m, a.ExpensePerDay);
    }
}

internal static class TransactionExtensions
{
    public static Transaction With(this Transaction t, Func<Transaction, Transaction> change) => change(t);
}

public class RecurrenceEngineTests
{
    private static RecurringTransaction T(RecurrenceFrequency f, int day, string start, string? end = null) => new()
    {
        Id = Guid.NewGuid(), Frequency = f, Day = day, StartDate = DateOnly.Parse(start), EndDate = end is null ? null : DateOnly.Parse(end), Amount = 1,
    };

    private static List<string> Dates(RecurringTransaction t, string from, string to) =>
        RecurrenceEngine.Occurrences(t, DateOnly.Parse(from), DateOnly.Parse(to)).Select(d => d.ToString("yyyy-MM-dd")).ToList();

    [Fact]
    public void Monthly_ShortensTheDayToTheLastDayOfShortMonths()
    {
        var t = T(RecurrenceFrequency.Monthly, 31, "2026-01-01");
        Assert.Equal(["2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30"], Dates(t, "2026-01-01", "2026-04-30"));
        Assert.Equal("2028-02-29", Dates(t, "2028-02-01", "2028-02-29").Single());
    }

    [Fact]
    public void Monthly_RespectsTheFromDate_TheStartDate_AndTheEndDate()
    {
        var t = T(RecurrenceFrequency.Monthly, 15, "2026-01-20", end: "2026-06-30");
        Assert.Equal(["2026-02-15", "2026-03-15"], Dates(t, "2026-01-01", "2026-03-31"));
        Assert.Equal(["2026-04-15", "2026-05-15", "2026-06-15"], Dates(t, "2026-03-20", "2026-12-31"));
        Assert.Empty(Dates(t, "2026-07-01", "2026-12-31"));
    }

    [Fact]
    public void Weekly_FallsOnTheChosenIsoWeekday()
    {
        var mondays = T(RecurrenceFrequency.Weekly, 1, "2026-01-01");
        Assert.Equal(["2026-10-05", "2026-10-12", "2026-10-19", "2026-10-26"], Dates(mondays, "2026-10-01", "2026-10-31"));

        var sundays = T(RecurrenceFrequency.Weekly, 7, "2026-10-01");
        Assert.Equal(["2026-10-04", "2026-10-11"], Dates(sundays, "2026-10-01", "2026-10-11"));
    }

    [Fact]
    public void Yearly_UsesTheStartMonth_AndMovesFebruary29ToTheLastDay()
    {
        var t = T(RecurrenceFrequency.Yearly, 29, "2024-02-29");
        Assert.Equal(["2024-02-29", "2025-02-28", "2026-02-28", "2027-02-28", "2028-02-29"], Dates(t, "2024-01-01", "2028-12-31"));
    }

    [Fact]
    public void EmptyRange_GivesNothing()
    {
        var t = T(RecurrenceFrequency.Monthly, 1, "2026-05-01", end: "2026-04-01");
        Assert.Empty(Dates(t, "2026-01-01", "2026-12-31"));
        Assert.Empty(Dates(T(RecurrenceFrequency.Monthly, 1, "2026-05-01"), "2026-12-31", "2026-01-01"));
    }
}

public class BudgetAndRecurringServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-p4-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore(FakeRateSource? rates = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        var c = BudgetCore.Create(Path.Combine(_root, "l" + _cores.Count), Path.Combine(_root, "shared"), "x", rateSource: rates ?? new FakeRateSource());
        _cores.Add(c);
        return c;
    }

    private static readonly DateOnly Today = new(2026, 10, 24);

    [Fact]
    public void SetLimit_ReplacesTheEntryOfTheSameMonth_AndKeepsHistoryAcrossMonths()
    {
        var core = NewCore();
        var cat = core.Categories.Add("Test", null, CategoryType.Expense);

        core.Budgets.SetLimit(cat.Id, 500, new DateOnly(2026, 3, 17));
        core.Budgets.SetLimit(cat.Id, 600, new DateOnly(2026, 3, 2));   // same month: replaces
        core.Budgets.SetLimit(cat.Id, 800, new DateOnly(2026, 6, 1));

        var history = core.Budgets.History(cat.Id);
        Assert.Equal([600m, 800m], history.Select(b => b.MonthlyAmount));
        Assert.Equal(new DateOnly(2026, 3, 1), history[0].ValidFromMonth);
        Assert.Equal(600m, BudgetMath.Effective(core.Reader.GetBudgets(), new DateOnly(2026, 5, 1))[cat.Id]);
    }

    [Fact]
    public void CopyFromPreviousMonth_CopiesInForceLimits_AndRunningItAgainChangesNothing()
    {
        var core = NewCore();
        var a = core.Categories.Add("A", null, CategoryType.Expense);
        var b = core.Categories.Add("B", null, CategoryType.Expense);
        core.Budgets.SetLimit(a.Id, 100, new DateOnly(2026, 9, 1));
        core.Budgets.SetLimit(b.Id, 200, new DateOnly(2026, 1, 1));   // started long ago, still in force in September
        core.Budgets.SetLimit(b.Id, 0, new DateOnly(2026, 9, 1));      // switched off in September
        core.Budgets.SetLimit(a.Id, 150, new DateOnly(2026, 10, 1));   // already set for October with another value

        var copied = core.Budgets.CopyFromPreviousMonth(new DateOnly(2026, 10, 1));
        Assert.Equal(1, copied);                                       // only A differs; B has no limit in September
        Assert.Equal(100m, BudgetMath.Effective(core.Reader.GetBudgets(), new DateOnly(2026, 10, 1))[a.Id]);

        Assert.Equal(0, core.Budgets.CopyFromPreviousMonth(new DateOnly(2026, 10, 1)));
        var november = core.Budgets.CopyFromPreviousMonth(new DateOnly(2026, 11, 1));
        Assert.Equal(0, november); // November already inherits October's limit
    }

    private static RecurringTransaction Rent(DateOnly start, string currency = "PLN", decimal amount = 1200m) => new()
    {
        Type = TransactionType.Expense, Amount = amount, CurrencyCode = currency, Frequency = RecurrenceFrequency.Monthly, Day = 5,
        StartDate = start, Place = "Spółdzielnia", Note = "czynsz",
    };

    [Fact]
    public async Task Generate_CreatesEveryDueOccurrence_Once()
    {
        var core = NewCore();
        var cat = core.Categories.Add("Rachunki", null, CategoryType.Expense);
        var t = core.Recurring.Add(Rent(new DateOnly(2026, 7, 5)) with { CategoryId = cat.Id });

        var run = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(4, run.Created);                              // 5 Jul, Aug, Sep, Oct

        var list = core.Reader.GetTransactions();
        Assert.Equal(4, list.Count);
        Assert.All(list, x =>
        {
            Assert.Equal(1200m, x.AmountPln);
            Assert.Equal("Spółdzielnia", x.Place);
            Assert.Equal("czynsz", x.Note);
            Assert.Equal(cat.Id, x.CategoryId);
            Assert.Equal(DeterministicId.ForRecurrence(t.Id, x.Date), x.Id);
            Assert.Equal(5, x.Date.Day);
        });
        Assert.Equal(new DateOnly(2026, 10, 5), core.Reader.GetRecurringTransactions().Single().LastGeneratedDate);

        var again = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(0, again.Created);
        Assert.Equal(4, core.Reader.GetTransactions().Count);

        var nextMonth = await core.Recurring.GenerateDueAsync(new DateOnly(2026, 11, 5));
        Assert.Equal(1, nextMonth.Created);
    }

    [Fact]
    public async Task Generate_WithConfirmation_BooksNothingUntilTheUserAnswers()
    {
        var core = NewCore();
        var t = core.Recurring.Add(Rent(new DateOnly(2026, 8, 5)) with { ConfirmEach = true });

        var run = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(0, run.Created);
        Assert.Equal([new DateOnly(2026, 8, 5), new DateOnly(2026, 9, 5), new DateOnly(2026, 10, 5)], run.Pending.Select(p => p.Date));
        Assert.Empty(core.Reader.GetTransactions());
        Assert.Null(core.Reader.GetRecurringTransactions().Single().LastGeneratedDate);   // nothing was handled yet

        // yes: the same deterministic transaction an automatic run would have made
        Assert.True(await core.Recurring.ConfirmAsync(t.Id, new DateOnly(2026, 8, 5)));
        var booked = core.Reader.GetTransactions().Single();
        Assert.Equal(DeterministicId.ForRecurrence(t.Id, new DateOnly(2026, 8, 5)), booked.Id);
        Assert.Equal(1200m, booked.AmountPln);

        // no: never booked, never asked again
        core.Recurring.Decline(t.Id, new DateOnly(2026, 9, 5));
        var after = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal([new DateOnly(2026, 10, 5)], after.Pending.Select(p => p.Date));
        Assert.Single(core.Reader.GetTransactions());

        Assert.True(await core.Recurring.ConfirmAsync(t.Id, new DateOnly(2026, 10, 5)));
        var done = await core.Recurring.GenerateDueAsync(Today);
        Assert.Empty(done.Pending);
        Assert.Equal(2, core.Reader.GetTransactions().Count);
        Assert.Equal(new DateOnly(2026, 10, 5), core.Reader.GetRecurringTransactions().Single().LastGeneratedDate);   // all answered: the watermark moved on
        Assert.Equal([new DateOnly(2026, 9, 5)], core.Reader.GetRecurringTransactions().Single().SkippedDates);
    }

    [Fact]
    public async Task Generate_WithConfirmation_TheFlagAndDeclinedDatesSurviveARebuildFromTheJournal()
    {
        var core = NewCore();
        var t = core.Recurring.Add(Rent(new DateOnly(2026, 9, 5)) with { ConfirmEach = true });
        core.Recurring.Decline(t.Id, new DateOnly(2026, 9, 5));

        var stored = core.Reader.GetRecurringTransactions().Single();
        Assert.True(stored.ConfirmEach);
        Assert.Equal([new DateOnly(2026, 9, 5)], stored.SkippedDates);

        core.Sync.Rebuild();
        var rebuilt = core.Reader.GetRecurringTransactions().Single();
        Assert.True(rebuilt.ConfirmEach);
        Assert.Equal([new DateOnly(2026, 9, 5)], rebuilt.SkippedDates);
    }

    [Fact]
    public async Task TwoComputersGeneratingTheSameTemplate_EndWithOneTransactionPerDate()
    {
        var a = NewCore();
        var b = NewCore();
        a.Recurring.Add(Rent(new DateOnly(2026, 7, 5)));
        a.Sync.SyncOnce(); b.Sync.SyncOnce();
        Assert.Single(b.Reader.GetRecurringTransactions());

        await a.Recurring.GenerateDueAsync(Today);
        await b.Recurring.GenerateDueAsync(Today);     // b has not seen a's transactions yet
        a.Sync.SyncOnce(); b.Sync.SyncOnce(); a.Sync.SyncOnce();

        Assert.Equal(4, a.Reader.GetTransactions().Count);
        Assert.Equal(4, b.Reader.GetTransactions().Count);
        Assert.Equal(a.Reader.GetTransactions().Select(x => x.Id).Order(), b.Reader.GetTransactions().Select(x => x.Id).Order());
        Assert.Equal(a.Reader.GetRecurringTransactions().Single().LastGeneratedDate, b.Reader.GetRecurringTransactions().Single().LastGeneratedDate);
    }

    [Fact]
    public async Task ADeletedGeneratedTransaction_IsNotCreatedAgain()
    {
        var core = NewCore();
        var t = core.Recurring.Add(Rent(new DateOnly(2026, 7, 5)));
        await core.Recurring.GenerateDueAsync(Today);

        var august = core.Reader.GetTransactions().Single(x => x.Date == new DateOnly(2026, 8, 5));
        core.Transactions.Delete([august]);
        core.Recurring.Save(core.Reader.GetRecurringTransactions().Single() with { LastGeneratedDate = null }); // force a full re-check

        var run = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(0, run.Created);
        Assert.Equal(3, core.Reader.GetTransactions().Count);
        Assert.DoesNotContain(core.Reader.GetTransactions(), x => x.Date == new DateOnly(2026, 8, 5));
    }

    [Fact]
    public async Task EndDateAndDeletedTemplate_StopGeneration()
    {
        var core = NewCore();
        core.Recurring.Add(Rent(new DateOnly(2026, 7, 5)) with { EndDate = new DateOnly(2026, 8, 31) });
        await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(2, core.Reader.GetTransactions().Count);

        var t = core.Reader.GetRecurringTransactions().Single();
        core.Recurring.Delete(t.Id);
        var run = await core.Recurring.GenerateDueAsync(new DateOnly(2027, 1, 1));
        Assert.Equal(0, run.Created);
        Assert.Equal(2, core.Reader.GetTransactions().Count);   // history stays
    }

    [Fact]
    public async Task ForeignCurrencyTemplate_UsesTheNbpRateOfEachDate()
    {
        var rates = new FakeRateSource(("2026-09-04", 3.90m), ("2026-09-30", 4.00m), ("2026-10-02", 4.10m));
        var core = NewCore(rates);
        core.Recurring.Add(Rent(new DateOnly(2026, 9, 5), "EUR", 10m) with { Day = 5 });

        var run = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(2, run.Created);

        var list = core.Reader.GetTransactions().OrderBy(x => x.Date).ToList();
        Assert.Equal(new DateOnly(2026, 9, 5), list[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 5), list[1].Date);
        Assert.Equal(new DateOnly(2026, 9, 4), list[0].ExchangeRateDate);
        Assert.Equal(39.00m, list[0].AmountPln);
        Assert.Equal(new DateOnly(2026, 10, 2), list[1].ExchangeRateDate);
        Assert.Equal(41.00m, list[1].AmountPln);
        Assert.Equal("EUR", list[1].CurrencyCode);
    }

    [Fact]
    public async Task ForeignCurrencyTemplate_WithoutARate_WaitsInsteadOfBookingAWrongAmount()
    {
        var core = NewCore(new FakeRateSource { Offline = true });
        var t = core.Recurring.Add(Rent(new DateOnly(2026, 9, 5), "USD", 10m));

        var run = await core.Recurring.GenerateDueAsync(Today);
        Assert.Equal(0, run.Created);
        Assert.Equal([t.Id], run.TemplatesWaitingForRate);
        Assert.Empty(core.Reader.GetTransactions());
        Assert.Null(core.Reader.GetRecurringTransactions().Single().LastGeneratedDate);
    }
}
