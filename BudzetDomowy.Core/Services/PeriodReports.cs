using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Services;

public sealed record PeriodSummary(DateOnly From, DateOnly To, decimal Income, decimal Expense, int Count)
{
    public decimal Balance => Income - Expense;
}

/// <param name="Delta">Current minus previous.</param>
/// <param name="Percent">Change relative to the previous value; null when that was zero.</param>
public sealed record Change(decimal Current, decimal Previous)
{
    public decimal Delta => Current - Previous;
    public decimal? Percent => Previous == 0 ? null : Delta / Math.Abs(Previous);
}

public sealed record PeriodComparison(PeriodSummary Current, PeriodSummary Previous)
{
    public Change Income => new(Current.Income, Previous.Income);
    public Change Expense => new(Current.Expense, Previous.Expense);
    public Change Balance => new(Current.Balance, Previous.Balance);
}

/// <param name="IsPartial">The ranges were cut: the current period is not over, or the previous one has no data for its whole length.</param>
public sealed record AvailableComparison(PeriodComparison Comparison, bool IsPartial);

public sealed record MonthPoint(DateOnly Month, decimal Income, decimal Expense)
{
    public decimal Balance => Income - Expense;
}

/// <param name="IsOwn">A child that stands for the amount booked directly on the parent category.</param>
public sealed record BreakdownItem(Guid? CategoryId, string Name, string Color, decimal Amount, decimal Share, bool IsOwn, IReadOnlyList<BreakdownItem> Children);

public enum IncompleteMonthMode
{
    /// <summary>Months count in proportion to the days that are over (the current month 10/31 counts 0.32).</summary>
    Prorate,
    /// <summary>Only whole months that are completely over and inside the range are used.</summary>
    ExcludeIncomplete,
}

/// <param name="Days">Days the totals cover: the range cut at today, or only the whole months used in the exclude mode.</param>
/// <param name="Months">Number of months the totals are divided by (see <see cref="IncompleteMonthMode"/>).</param>
public sealed record Averages(
    DateOnly From, DateOnly To, int Days, decimal Months,
    decimal Expense, decimal Income,
    decimal? ExpensePerDay, decimal? ExpensePerWeek, decimal? ExpensePerMonth,
    decimal? IncomePerMonth, IncompleteMonthMode Mode);

public static class PeriodReports
{
    public static DateOnly MonthEnd(DateOnly month) => new DateOnly(month.Year, month.Month, 1).AddMonths(1).AddDays(-1);

    public static PeriodSummary Summary(IEnumerable<Transaction> all, DateOnly from, DateOnly to)
    {
        decimal income = 0, expense = 0;
        var count = 0;
        foreach (var t in all.Where(t => t.Date >= from && t.Date <= to))
        {
            count++;
            if (t.Type == TransactionType.Income) income += t.AmountPln; else expense += t.AmountPln;
        }
        return new PeriodSummary(from, to, income, expense, count);
    }

    public static PeriodSummary MonthSummary(IEnumerable<Transaction> all, DateOnly month) =>
        Summary(all, new DateOnly(month.Year, month.Month, 1), MonthEnd(month));

    public static PeriodSummary YearSummary(IEnumerable<Transaction> all, int year) =>
        Summary(all, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    /// <summary>The same range one month / one year earlier.</summary>
    public static PeriodComparison CompareMonth(IEnumerable<Transaction> all, DateOnly month)
    {
        var list = all as IReadOnlyCollection<Transaction> ?? all.ToList();
        return new PeriodComparison(MonthSummary(list, month), MonthSummary(list, month.AddMonths(-1)));
    }

    /// <summary>
    /// Compares a month or year with the one before it, but only over the part both have: a running period is cut at today and the
    /// previous period at the same day one month / year earlier; where the data starts later than the previous period begins, both
    /// start at the same later date. The result says when something was cut off (the overview then warns that it is incomplete).
    /// </summary>
    public static AvailableComparison CompareAvailable(IEnumerable<Transaction> all, PeriodKind kind, DateOnly anchor, DateOnly today)
    {
        var list = all as IReadOnlyCollection<Transaction> ?? all.ToList();
        var (p0, p1) = PeriodRanges.Range(kind, anchor);
        var (q0, q1) = PeriodRanges.Range(kind, PeriodRanges.Shift(kind, anchor, -1));

        var end = p1 < today ? p1 : today;                                    // the days of the current period that happened
        // "the same day a year / month ago"; a finished period is compared with the whole previous one
        var previousEnd = end == p1 ? q1 : PeriodRanges.Shift(kind, end, -1);
        if (previousEnd > q1) previousEnd = q1;

        var start = p0;
        var previousStart = q0;
        if (list.Count > 0)
        {
            var first = list.Min(t => t.Date);
            if (first > q0)
            {
                previousStart = first;                                         // the previous period has data only from here
                var aligned = PeriodRanges.Shift(kind, first, 1);
                start = aligned > p0 ? aligned : p0;
            }
        }

        var current = Summary(list, start, end);
        var previous = Summary(list, previousStart, previousEnd);
        var partial = start > p0 || end < p1 || previousStart > q0 || previousEnd < q1;
        return new AvailableComparison(new PeriodComparison(current, previous), partial);
    }

    public static PeriodComparison CompareYear(IEnumerable<Transaction> all, int year)
    {
        var list = all as IReadOnlyCollection<Transaction> ?? all.ToList();
        return new PeriodComparison(YearSummary(list, year), YearSummary(list, year - 1));
    }

    /// <summary>Income and expenses of every month from <paramref name="firstMonth"/> to <paramref name="lastMonth"/>, empty months included.</summary>
    public static IReadOnlyList<MonthPoint> MonthlyTrend(IEnumerable<Transaction> all, DateOnly firstMonth, DateOnly lastMonth)
    {
        var from = new DateOnly(firstMonth.Year, firstMonth.Month, 1);
        var last = new DateOnly(lastMonth.Year, lastMonth.Month, 1);
        var sums = all.Where(t => t.Date >= from && t.Date <= MonthEnd(last))
            .GroupBy(t => new DateOnly(t.Date.Year, t.Date.Month, 1))
            .ToDictionary(g => g.Key, g => (Income: g.Where(t => t.Type == TransactionType.Income).Sum(t => t.AmountPln),
                                              Expense: g.Where(t => t.Type == TransactionType.Expense).Sum(t => t.AmountPln)));
        var points = new List<MonthPoint>();
        for (var m = from; m <= last; m = m.AddMonths(1))
        {
            var (income, expense) = sums.GetValueOrDefault(m);
            points.Add(new MonthPoint(m, income, expense));
        }
        return points;
    }

    /// <summary>
    /// Tree of spending by category. A parent's amount includes its subcategories; amounts booked directly on a parent
    /// that also has subcategories appear as an extra "own" child, so the children always add up to the parent.
    /// </summary>
    public static IReadOnlyList<BreakdownItem> Breakdown(
        IEnumerable<Transaction> all, IEnumerable<Category> categories, DateOnly from, DateOnly to, TransactionType type)
    {
        var cats = categories.ToList();
        var ids = cats.Select(c => c.Id).ToHashSet();
        var own = new Dictionary<Guid, decimal>();
        decimal uncategorized = 0;
        foreach (var t in all.Where(t => t.Type == type && t.Date >= from && t.Date <= to))
        {
            if (t.CategoryId is { } id && ids.Contains(id)) own[id] = own.GetValueOrDefault(id) + t.AmountPln;
            else uncategorized += t.AmountPln;
        }

        var byParent = cats.ToLookup(c => c.ParentId is { } p && ids.Contains(p) ? p : Guid.Empty);

        BreakdownItem? Build(Category c)
        {
            var children = byParent[c.Id].Select(Build).Where(x => x is not null).Select(x => x!).ToList();
            var ownAmount = own.GetValueOrDefault(c.Id);
            var total = ownAmount + children.Sum(x => x.Amount);
            if (total == 0) return null;
            if (children.Count > 0 && ownAmount > 0)
                children.Add(new BreakdownItem(c.Id, c.Name, c.Color, ownAmount, 0, true, []));
            return new BreakdownItem(c.Id, c.Name, c.Color, total, 0, false,
                children.OrderByDescending(x => x.Amount).ToList());
        }

        var roots = byParent[Guid.Empty].Select(Build).Where(x => x is not null).Select(x => x!).ToList();
        if (uncategorized > 0) roots.Add(new BreakdownItem(null, "", "", uncategorized, 0, false, []));

        var grand = roots.Sum(r => r.Amount);
        return roots.OrderByDescending(r => r.Amount).Select(r => WithShares(r, grand)).ToList();
    }

    private static BreakdownItem WithShares(BreakdownItem item, decimal parentTotal) => item with
    {
        Share = parentTotal == 0 ? 0 : item.Amount / parentTotal,
        Children = item.Children.Select(c => WithShares(c, item.Amount)).ToList(),
    };

    public static IReadOnlyList<BreakdownItem> TopCategories(IReadOnlyList<BreakdownItem> breakdown, int count) =>
        breakdown.Take(count).ToList();

    /// <summary>
    /// Average spending over a date range. The range is cut at today (days that have not happened are not counted).
    /// Per day = expenses / days; per week = expenses / (days / 7); per month = expenses / months, where months are counted
    /// by <paramref name="mode"/>: proportionally for a partial month, or only whole finished months.
    /// </summary>
    public static Averages Average(IEnumerable<Transaction> all, DateOnly from, DateOnly to, DateOnly today, IncompleteMonthMode mode)
    {
        var end = to < today ? to : today;
        if (end < from)
            return new Averages(from, to, 0, 0, 0, 0, null, null, null, null, mode);

        var list = all as IReadOnlyCollection<Transaction> ?? all.ToList();
        decimal months = 0;
        DateOnly usedFrom = from, usedTo = end;
        if (mode == IncompleteMonthMode.Prorate)
        {
            for (var m = new DateOnly(from.Year, from.Month, 1); m <= end; m = m.AddMonths(1))
            {
                var first = m < from ? from : m;
                var lastOfMonth = MonthEnd(m);
                var last = lastOfMonth > end ? end : lastOfMonth;
                months += (decimal)(last.DayNumber - first.DayNumber + 1) / DateTime.DaysInMonth(m.Year, m.Month);
            }
        }
        else
        {
            // whole months only: the first one starting on/after `from`, the last one finishing on/before the end (and before today)
            var first = from.Day == 1 ? from : new DateOnly(from.Year, from.Month, 1).AddMonths(1);
            var lastDay = end;
            var lastWhole = MonthEnd(new DateOnly(lastDay.Year, lastDay.Month, 1)) == lastDay
                ? new DateOnly(lastDay.Year, lastDay.Month, 1)
                : new DateOnly(lastDay.Year, lastDay.Month, 1).AddMonths(-1);
            for (var m = first; m <= lastWhole; m = m.AddMonths(1)) months++;
            if (months > 0)
            {
                usedFrom = first;
                usedTo = MonthEnd(lastWhole);
            }
        }

        // totals over the whole counted range; for "exclude incomplete" the totals cover only the whole months used
        var totalsFrom = mode == IncompleteMonthMode.Prorate || months == 0 ? from : usedFrom;
        var totalsTo = mode == IncompleteMonthMode.Prorate || months == 0 ? end : usedTo;
        var s = Summary(list, totalsFrom, totalsTo);
        var days = totalsTo.DayNumber - totalsFrom.DayNumber + 1;

        decimal? perDay = days > 0 ? R(s.Expense / days) : null;
        decimal? perWeek = days > 0 ? R(s.Expense / ((decimal)days / 7m)) : null;
        decimal? perMonth = months > 0 ? R(s.Expense / months) : null;
        decimal? incomePerMonth = months > 0 ? R(s.Income / months) : null;
        return new Averages(from, to, days, Math.Round(months, 4), s.Expense, s.Income, perDay, perWeek, perMonth, incomePerMonth, mode);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
