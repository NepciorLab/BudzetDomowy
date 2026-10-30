using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

public enum BudgetStatus { Ok, Warning, Over }

/// <param name="Fraction">Spent / limit (1.0 = limit reached).</param>
public sealed record BudgetLine(Category Category, string Path, decimal Limit, decimal Spent, decimal Fraction, BudgetStatus Status)
{
    public decimal Remaining => Limit - Spent;
}

public static class BudgetMath
{
    /// <summary>Warning starts here (80% of the limit).</summary>
    public const decimal WarningFraction = 0.8m;

    public static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>
    /// Limits valid in the month: per category the newest entry that starts in or before the month.
    /// An entry with amount 0 switches the limit off from that month on.
    /// </summary>
    public static IReadOnlyDictionary<Guid, decimal> Effective(IEnumerable<Budget> budgets, DateOnly month)
    {
        var m = MonthStart(month);
        return budgets
            .Where(b => b.ValidFromMonth <= m)
            .GroupBy(b => b.CategoryId)
            .Select(g => g.OrderByDescending(b => b.ValidFromMonth).ThenBy(b => b.Id).First())
            .Where(b => b.MonthlyAmount > 0)
            .ToDictionary(b => b.CategoryId, b => b.MonthlyAmount);
    }

    /// <summary>The yearly limits valid in the month (the same rule as for the monthly ones: the newest record that starts in or before the month).</summary>
    public static IReadOnlyDictionary<Guid, decimal> EffectiveYearly(IEnumerable<Budget> budgets, DateOnly month)
    {
        var m = MonthStart(month);
        return budgets
            .Where(b => b.ValidFromMonth <= m)
            .GroupBy(b => b.CategoryId)
            .Select(g => g.OrderByDescending(b => b.ValidFromMonth).ThenBy(b => b.Id).First())
            .Where(b => b.YearlyAmount > 0)
            .ToDictionary(b => b.CategoryId, b => b.YearlyAmount);
    }

    public static BudgetStatus StatusOf(decimal fraction) =>
        fraction > 1m ? BudgetStatus.Over : fraction >= WarningFraction ? BudgetStatus.Warning : BudgetStatus.Ok;

    /// <summary>
    /// Spending against each limit in the month. A limit on a parent category counts its subcategories too.
    /// Spending in any currency counts by its PLN amount.
    /// </summary>
    public static IReadOnlyList<BudgetLine> Progress(
        DateOnly month, IEnumerable<Budget> budgets, IEnumerable<Category> categories, IEnumerable<Transaction> transactions)
    {
        var cats = categories.ToList();
        var byId = cats.ToDictionary(c => c.Id);
        var start = MonthStart(month);
        var end = start.AddMonths(1).AddDays(-1);
        var expenses = transactions.Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end).ToList();

        var lines = new List<BudgetLine>();
        foreach (var (categoryId, limit) in Effective(budgets, month))
        {
            if (!byId.TryGetValue(categoryId, out var cat)) continue;
            var ids = CategoryHelpers.WithDescendants(categoryId, cats);
            var spent = expenses.Where(t => t.CategoryId is { } c && ids.Contains(c)).Sum(t => t.AmountPln);
            var fraction = spent / limit;
            lines.Add(new BudgetLine(cat, CategoryHelpers.PathOf(categoryId, byId), limit, spent, fraction, StatusOf(fraction)));
        }
        return lines.OrderByDescending(l => l.Fraction).ThenBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// What was spent in the calendar year of <paramref name="month"/> against the yearly limits valid in that month
    /// (subcategories count towards their parent, as with the monthly ones).
    /// </summary>
    public static IReadOnlyList<BudgetLine> YearlyProgress(
        DateOnly month, IEnumerable<Budget> budgets, IEnumerable<Category> categories, IEnumerable<Transaction> transactions)
    {
        var cats = categories.ToList();
        var byId = cats.ToDictionary(c => c.Id);
        var expenses = transactions.Where(t => t.Type == TransactionType.Expense && t.Date.Year == month.Year).ToList();

        var lines = new List<BudgetLine>();
        foreach (var (categoryId, limit) in EffectiveYearly(budgets, month))
        {
            if (!byId.TryGetValue(categoryId, out var cat)) continue;
            var ids = CategoryHelpers.WithDescendants(categoryId, cats);
            var spent = expenses.Where(t => t.CategoryId is { } c && ids.Contains(c)).Sum(t => t.AmountPln);
            var fraction = spent / limit;
            lines.Add(new BudgetLine(cat, CategoryHelpers.PathOf(categoryId, byId), limit, spent, fraction, StatusOf(fraction)));
        }
        return lines.OrderByDescending(l => l.Fraction).ThenBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// A whole year against its limits (the Dashboard). A category with a yearly limit is measured by it: what was spent in the year
    /// against that limit. One that has only monthly limits is measured by their sum (of the months in which it had one, up to the running
    /// month in the present year) against what was spent in those months.
    /// </summary>
    public static IReadOnlyList<BudgetLine> YearProgress(
        int year, DateOnly today, IEnumerable<Budget> budgets, IEnumerable<Category> categories, IEnumerable<Transaction> transactions)
    {
        var list = budgets.ToList();
        var cats = categories.ToList();
        var all = transactions.ToList();
        var last = year == today.Year ? today.Month : 12;

        var lines = YearlyProgress(new DateOnly(year, last, 1), list, cats, all).ToList();
        var measuredYearly = lines.Select(l => l.Category.Id).ToHashSet();

        var monthly = new Dictionary<Guid, (BudgetLine First, decimal Limit, decimal Spent)>();
        for (var m = 1; m <= last; m++)
            foreach (var line in Progress(new DateOnly(year, m, 1), list, cats, all))
            {
                if (measuredYearly.Contains(line.Category.Id)) continue;
                monthly[line.Category.Id] = monthly.TryGetValue(line.Category.Id, out var so)
                    ? (so.First, so.Limit + line.Limit, so.Spent + line.Spent)
                    : (line, line.Limit, line.Spent);
            }

        lines.AddRange(monthly.Values.Select(t =>
            new BudgetLine(t.First.Category, t.First.Path, t.Limit, t.Spent, t.Spent / t.Limit, StatusOf(t.Spent / t.Limit))));
        return lines.OrderByDescending(l => l.Fraction).ThenBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

public sealed class BudgetService
{
    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;

    public BudgetService(EventRecorder recorder, LocalReader reader)
    {
        _recorder = recorder;
        _reader = reader;
    }

    /// <summary>One entry per category and month, so changing it again in the same month replaces it instead of piling up.</summary>
    public static Guid IdFor(Guid categoryId, DateOnly month) =>
        DeterministicId.FromString($"budget:{categoryId:D}:{month:yyyy-MM}");

    /// <summary>Sets the monthly limit valid from the month on (the yearly one stays as it is). Amount 0 removes the limit from that month.</summary>
    public Budget SetLimit(Guid categoryId, decimal monthlyAmount, DateOnly month) => Write(categoryId, monthlyAmount, null, month);

    /// <summary>Sets the yearly limit valid from the month on (the monthly one stays as it is). Amount 0 removes the limit from that month.</summary>
    public Budget SetYearlyLimit(Guid categoryId, decimal yearlyAmount, DateOnly month) => Write(categoryId, null, yearlyAmount, month);

    private static decimal Clean(decimal amount) => Math.Max(0, Math.Round(amount, 2, MidpointRounding.AwayFromZero));

    /// <summary>One record holds both limits: the one that is not being changed is taken over from the record that is valid in the month.</summary>
    private Budget Write(Guid categoryId, decimal? monthly, decimal? yearly, DateOnly month)
    {
        var start = BudgetMath.MonthStart(month);
        var valid = _reader.GetBudgets()
            .Where(b => b.CategoryId == categoryId && b.ValidFromMonth <= start)
            .OrderByDescending(b => b.ValidFromMonth).ThenBy(b => b.Id).FirstOrDefault();
        var b = new Budget
        {
            Id = IdFor(categoryId, start),
            CategoryId = categoryId,
            MonthlyAmount = monthly is { } m ? Clean(m) : valid?.MonthlyAmount ?? 0,
            YearlyAmount = yearly is { } y ? Clean(y) : valid?.YearlyAmount ?? 0,
            ValidFromMonth = start,
        };
        _recorder.Save(b);
        return b;
    }

    /// <summary>Copies every limit that was in force in the previous month into this month. Returns how many were written.</summary>
    public int CopyFromPreviousMonth(DateOnly month)
    {
        var start = BudgetMath.MonthStart(month);
        var all = _reader.GetBudgets();
        var before = start.AddMonths(-1);
        var monthlyBefore = BudgetMath.Effective(all, before);
        var yearlyBefore = BudgetMath.EffectiveYearly(all, before);
        var monthlyNow = BudgetMath.Effective(all, start);
        var yearlyNow = BudgetMath.EffectiveYearly(all, start);
        var count = 0;
        foreach (var categoryId in monthlyBefore.Keys.Union(yearlyBefore.Keys).ToList())
        {
            var monthly = monthlyBefore.GetValueOrDefault(categoryId);
            var yearly = yearlyBefore.GetValueOrDefault(categoryId);
            if (monthlyNow.GetValueOrDefault(categoryId) == monthly && yearlyNow.GetValueOrDefault(categoryId) == yearly) continue;
            Write(categoryId, monthly, yearly, start);
            count++;
        }
        return count;
    }

    /// <summary>Changes of the limit of one category, oldest first.</summary>
    public IReadOnlyList<Budget> History(Guid categoryId) =>
        _reader.GetBudgets().Where(b => b.CategoryId == categoryId).OrderBy(b => b.ValidFromMonth).ToList();
}
