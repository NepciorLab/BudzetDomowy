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
