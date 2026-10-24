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

    /// <summary>Sets the limit valid from the month on. Amount 0 removes the limit from that month.</summary>
    public Budget SetLimit(Guid categoryId, decimal monthlyAmount, DateOnly month)
    {
        var start = BudgetMath.MonthStart(month);
        var b = new Budget
        {
            Id = IdFor(categoryId, start),
            CategoryId = categoryId,
            MonthlyAmount = Math.Max(0, Math.Round(monthlyAmount, 2, MidpointRounding.AwayFromZero)),
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
        var previous = BudgetMath.Effective(all, start.AddMonths(-1));
        var current = BudgetMath.Effective(all, start);
        var count = 0;
        foreach (var (categoryId, amount) in previous)
        {
            if (current.TryGetValue(categoryId, out var now) && now == amount) continue;
            SetLimit(categoryId, amount, start);
            count++;
        }
        return count;
    }

    /// <summary>Changes of the limit of one category, oldest first.</summary>
    public IReadOnlyList<Budget> History(Guid categoryId) =>
        _reader.GetBudgets().Where(b => b.CategoryId == categoryId).OrderBy(b => b.ValidFromMonth).ToList();
}
