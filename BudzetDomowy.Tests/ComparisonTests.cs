using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

public class ComparisonTests
{
    private static readonly IReadOnlyList<Category> Cats = DefaultCategories.Create();
    private static Guid Id(string name) => Cats.First(c => c.Name == name).Id;

    [Fact]
    public void YearsByMonth_PutsEachYearInItsOwnSeries_AndFiltersByTypeAndCategory()
    {
        var tx = new[]
        {
            Make.Expense("2025-01-10", 100, Id("Spożywcze")), Make.Expense("2025-03-05", 40, Id("Piekarnia")),
            Make.Expense("2026-01-02", 60, Id("Spożywcze")), Make.Expense("2026-01-20", 15, Id("Auto")),
            Make.Income("2026-01-31", 5000), Make.Expense("2024-01-01", 999, Id("Spożywcze")),
        };

        var all = ComparisonReports.YearsByMonth(tx, Cats, [2026, 2025], TransactionType.Expense, null);
        Assert.Equal([2025, 2026], all.Select(s => s.Year));                 // sorted, 2024 not asked for
        Assert.Equal([100m, 0m, 40m], all[0].Months.Take(3));
        Assert.Equal(75m, all[1].Months[0]);
        Assert.Equal(12, all[0].Months.Length);

        var food = ComparisonReports.YearsByMonth(tx, Cats, [2025, 2026], TransactionType.Expense, Id("Spożywcze"));
        Assert.Equal(140m, food[0].Total);                                    // groceries and their subcategory "Piekarnia"
        Assert.Equal(60m, food[1].Total);                                     // Auto excluded

        var income = ComparisonReports.YearsByMonth(tx, Cats, [2026], TransactionType.Income, null);
        Assert.Equal(5000m, income.Single().Months[0]);
    }

    [Fact]
    public void MonthsByCategory_ComparesMonthsCategoryByCategory_BiggestFirst_WithOthersBucket()
    {
        var tx = new[]
        {
            Make.Expense("2026-09-03", 300, Id("Auto")), Make.Expense("2026-10-03", 100, Id("Auto")),
            Make.Expense("2026-09-04", 50, Id("Spożywcze")), Make.Expense("2026-10-04", 400, Id("Spożywcze")),
            Make.Expense("2026-10-05", 10, Id("Chemia")), Make.Expense("2026-09-05", 5, Id("Alkohol")),
        };

        var cmp = ComparisonReports.MonthsByCategory(tx, Cats, [new DateOnly(2026, 10, 15), new DateOnly(2026, 9, 1)], TransactionType.Expense, null, maxCategories: 2);

        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], cmp.Series.Select(s => s.Month));
        Assert.Equal(["Spożywcze", "Auto", ComparisonReports.OthersLabel], cmp.Labels);   // 450 > 400 > the rest
        Assert.Equal([50m, 300m, 5m], cmp.Series[0].Values);                             // September
        Assert.Equal([400m, 100m, 10m], cmp.Series[1].Values);                           // October: Chemia in "others"
        Assert.Equal(355m, cmp.Series[0].Total);
        Assert.Null(cmp.CategoryIds[^1]);
    }

    [Fact]
    public void PickLists_SortSiblingsAlphabetically_KeepingChildrenUnderTheirParent()
    {
        Category Cat(string name, int order, Guid? parent = null) => new() { Id = Guid.NewGuid(), Name = name, SortOrder = order, ParentId = parent, Type = CategoryType.Expense };
        var zeta = Cat("Zeta", 0);
        var alfa = Cat("Alfa", 1);
        var kids = new[] { Cat("Żółw", 0, zeta.Id), Cat("Bar", 1, zeta.Id) };
        var all = new List<Category> { zeta, alfa }.Concat(kids).ToList();

        var manual = CategoryHelpers.Flatten(all).Select(o => o.Category.Name);
        Assert.Equal(["Zeta", "Żółw", "Bar", "Alfa"], manual);                       // the tree keeps the user's own order

        var pick = CategoryHelpers.ForEntry(all, TransactionType.Expense).Select(o => o.Category.Name);
        Assert.Equal(["Alfa", "Zeta", "Bar", "Żółw"], pick);                         // pick lists: A to Z at every level, Polish letters in place
    }

    [Fact]
    public void CompareAvailable_CutsARunningYearAndItsPredecessorAtTheSameDay()
    {
        var tx = new[]
        {
            Make.Expense("2025-02-10", 100), Make.Expense("2025-09-20", 50), Make.Expense("2025-11-30", 400),   // 2025: November is after "a year ago today"
            Make.Expense("2026-01-15", 70), Make.Expense("2026-10-01", 30),
            Make.Income("2025-03-01", 1000), Make.Income("2026-03-01", 1200),
        };
        var today = new DateOnly(2026, 10, 25);

        var result = PeriodReports.CompareAvailable(tx, PeriodKind.Year, new DateOnly(2026, 6, 1), today);
        var c = result.Comparison;
        Assert.True(result.IsPartial);
        // the data of 2025 starts on 10 Feb, so both years are compared from 10 Feb, and the running year only up to today
        Assert.Equal((new DateOnly(2026, 2, 10), new DateOnly(2026, 10, 25)), (c.Current.From, c.Current.To));
        Assert.Equal((new DateOnly(2025, 2, 10), new DateOnly(2025, 10, 25)), (c.Previous.From, c.Previous.To));
        Assert.Equal(30m, c.Current.Expense);                                                                        // 15 Jan 2026 has no counterpart in 2025 and is left out
        Assert.Equal(150m, c.Previous.Expense);                                                                      // the 400 of 30 Nov is after "a year ago today"
        Assert.Equal(1200m, c.Current.Income);
    }

    [Fact]
    public void CompareAvailable_ACompleteMonthWithCompleteDataIsNotMarkedPartial()
    {
        var tx = new[] { Make.Expense("2026-07-02", 10), Make.Expense("2026-08-31", 20), Make.Expense("2026-09-05", 30) };
        var result = PeriodReports.CompareAvailable(tx, PeriodKind.Month, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 25));
        Assert.False(result.IsPartial);
        Assert.Equal(30m, result.Comparison.Current.Expense);
        Assert.Equal(20m, result.Comparison.Previous.Expense);

        var running = PeriodReports.CompareAvailable(tx, PeriodKind.Month, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10));
        Assert.True(running.IsPartial);                                                                              // 10 Sep against 10 Aug
        Assert.Equal(new DateOnly(2026, 8, 10), running.Comparison.Previous.To);

        Assert.False(PeriodReports.CompareAvailable([], PeriodKind.Year, new DateOnly(2020, 1, 1), new DateOnly(2026, 10, 25)).IsPartial);
    }

    [Fact]
    public void CumulativeAverages_AverageTheMonthsSoFar_AndLeaveFutureMonthsEmpty()
    {
        var months = new decimal[12];
        months[0] = 100; months[1] = 300; months[2] = 200; months[3] = 600;
        var year = new YearSeries(2026, months);

        var running = ComparisonReports.CumulativeAverages(year, new DateOnly(2026, 3, 20));
        Assert.Equal([100m, 200m, 200m], running.Take(3));                 // 100 / 100+300 / 100+300+200
        Assert.All(running.Skip(3), v => Assert.Null(v));                  // April and later have not happened

        var full = ComparisonReports.CumulativeAverages(year, new DateOnly(2027, 1, 5));
        Assert.Equal(300m, full[3]);                                       // (100+300+200+600) / 4
        Assert.Equal(100m, full[11]);                                      // 1200 / 12
        Assert.All(ComparisonReports.CumulativeAverages(new YearSeries(2028, new decimal[12]), new DateOnly(2027, 1, 5)), v => Assert.Null(v));
    }

    [Fact]
    public void MonthsByCategory_CountsEntriesWithoutACategory()
    {
        var tx = new[]
        {
            Make.Expense("2026-10-03", 50), Make.Expense("2026-09-03", 70), Make.Expense("2026-10-04", 20, Id("Auto")),
        };

        var cmp = ComparisonReports.MonthsByCategory(tx, Cats, [new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], TransactionType.Expense);
        var none = cmp.CategoryIds.ToList().IndexOf(null);
        Assert.True(none >= 0);
        Assert.Equal(70m, cmp.Series[0].Values[none]);
        Assert.Equal(50m, cmp.Series[1].Values[none]);
    }

    [Fact]
    public void MonthsByCategory_CanDrillIntoOneCategory()
    {
        var tx = new[]
        {
            Make.Expense("2026-10-01", 30, Id("Paliwo")), Make.Expense("2026-10-02", 20, Id("Serwis i naprawy")),
            Make.Expense("2026-10-03", 10, Id("Auto")), Make.Expense("2026-10-04", 999, Id("Spożywcze")),
        };
        var cmp = ComparisonReports.MonthsByCategory(tx, Cats, [new DateOnly(2026, 10, 1)], TransactionType.Expense, Id("Auto"));

        Assert.Equal(["Paliwo", "Serwis i naprawy", "Auto"], cmp.Labels);               // the last one is what was booked on "Auto" itself
        Assert.Equal([30m, 20m, 10m], cmp.Series.Single().Values);
        Assert.Empty(ComparisonReports.MonthsByCategory(tx, Cats, [new DateOnly(2026, 10, 1)], TransactionType.Expense, Id("Chemia")).Labels);
    }

    [Fact]
    public void MonthsByCategory_WorksForIncomeToo_AndForMonthsWithoutData()
    {
        var tx = new[] { Make.Tx("2026-10-01", TransactionType.Income, 6000, Id("Wynagrodzenie")) };
        var cmp = ComparisonReports.MonthsByCategory(tx, Cats, [new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], TransactionType.Income);
        Assert.Equal(["Wynagrodzenie"], cmp.Labels);
        Assert.Equal([0m], cmp.Series[0].Values);
        Assert.Equal([6000m], cmp.Series[1].Values);
    }

    [Fact]
    public void ByPlace_SumsPerPlace_IgnoringCaseAndSpaces_AndShowsTheCommonSpelling()
    {
        var tx = new[]
        {
            Make.Expense("2026-10-01", 10) with { Place = "Lewiatan" },
            Make.Expense("2026-10-02", 15) with { Place = "lewiatan " },
            Make.Expense("2026-10-03", 20) with { Place = "Lewiatan" },
            Make.Expense("2026-10-04", 30) with { Place = "Orlen" },
            Make.Expense("2026-10-05", 5) with { Place = "" },
            Make.Expense("2026-09-30", 999) with { Place = "Orlen" },                       // outside the period
            Make.Income("2026-10-06", 500) with { Place = "Firma" },
        };

        var places = ComparisonReports.ByPlace(tx, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TransactionType.Expense, "(brak)");

        Assert.Equal(["Lewiatan", "Orlen", "(brak)"], places.Select(p => p.Name));
        Assert.Equal([45m, 30m, 5m], places.Select(p => p.Amount));
        Assert.Equal([3, 1, 1], places.Select(p => p.Count));
        Assert.Equal(0.5625m, places[0].Share);                                              // 45 / 80

        var income = ComparisonReports.ByPlace(tx, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TransactionType.Income);
        Assert.Equal("Firma", Assert.Single(income).Name);
    }

    [Fact]
    public void WeeklyAverage_ForOneCategory_UsesDaysDividedBySeven()
    {
        var tx = new[]
        {
            Make.Expense("2026-10-01", 70, Id("Paliwo")),    // part of "Auto"
            Make.Expense("2026-10-08", 70, Id("Auto")),
            Make.Expense("2026-10-09", 999, Id("Spożywcze")),
            Make.Income("2026-10-10", 123),
        };
        var today = new DateOnly(2026, 10, 14); // 14 days since the 1st

        var auto = ComparisonReports.Average(tx, Cats, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), today,
            IncompleteMonthMode.Prorate, TransactionType.Expense, Id("Auto"));
        Assert.Equal(14, auto.Days);
        Assert.Equal(140m, auto.Total);
        Assert.Equal(10m, auto.PerDay);
        Assert.Equal(70m, auto.PerWeek);

        var allExpenses = ComparisonReports.Average(tx, Cats, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), today,
            IncompleteMonthMode.Prorate, TransactionType.Expense, null);
        Assert.Equal(1139m, allExpenses.Total);

        var income = ComparisonReports.Average(tx, Cats, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), today,
            IncompleteMonthMode.Prorate, TransactionType.Income, null);
        Assert.Equal(61.5m, income.PerWeek);                                                // 123 / 2 weeks
    }
}

public class PeriodRangeTests
{
    [Theory]
    [InlineData("2026-10-24", "2026-10-19", "2026-10-25")]   // Saturday: the week of Monday 19th
    [InlineData("2026-10-19", "2026-10-19", "2026-10-25")]   // Monday
    [InlineData("2026-10-25", "2026-10-19", "2026-10-25")]   // Sunday belongs to the week that started on Monday
    [InlineData("2026-01-01", "2025-12-29", "2026-01-04")]   // a week across the new year
    public void Week_RunsFromMondayToSunday(string date, string from, string to)
    {
        var (f, t) = PeriodRanges.Range(PeriodKind.Week, DateOnly.Parse(date));
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), (f, t));
    }

    [Fact]
    public void MonthAndYear_CoverTheWholePeriod()
    {
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), PeriodRanges.Range(PeriodKind.Month, new DateOnly(2028, 2, 10)));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), PeriodRanges.Range(PeriodKind.Year, new DateOnly(2026, 6, 15)));
    }

    [Fact]
    public void Shift_StepsByWholePeriods_BothWays()
    {
        var d = new DateOnly(2026, 3, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), PeriodRanges.Shift(PeriodKind.Month, d, -1));   // no 31st in February
        Assert.Equal(new DateOnly(2026, 4, 7), PeriodRanges.Shift(PeriodKind.Week, d, 1));
        Assert.Equal(new DateOnly(2025, 3, 31), PeriodRanges.Shift(PeriodKind.Year, d, -1));
        Assert.Equal(43, PeriodRanges.IsoWeek(new DateOnly(2026, 10, 24)));
    }
}
