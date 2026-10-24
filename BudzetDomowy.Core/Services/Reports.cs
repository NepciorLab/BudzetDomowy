using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Services;

/// <param name="CategoryId">Top-level category the amount was rolled up to; null for entries without a category.</param>
public sealed record CategorySlice(Guid? CategoryId, string Name, string Color, decimal AmountPln, decimal Share);

/// <summary>Sums per category id plus everything entered without a category.</summary>
public sealed record CategoryTotals(IReadOnlyDictionary<Guid, decimal> ByCategory, decimal Uncategorized)
{
    public decimal Total => ByCategory.Values.Sum() + Uncategorized;
}

public sealed record EventReport(
    SpecialEvent Event,
    int TransactionCount,
    decimal ExpensePln,
    decimal IncomePln,
    /// <summary>Days of the event that have already started (counted to today at most).</summary>
    int Days,
    decimal? ExpensePerDay,
    decimal? PlannedPln,
    /// <summary>Spent / planned (1.0 = whole budget used); null without a plan.</summary>
    decimal? PlannedUsedFraction,
    decimal? RemainingPln,
    IReadOnlyList<CategorySlice> ByCategory,
    IReadOnlyList<Transaction> Transactions);

public static class Reports
{
    /// <summary>
    /// Sums of one type by category over a date range. Transactions that belong to a special event are
    /// counted like any other: an event is an extra dimension, it never hides a transaction from category totals.
    /// </summary>
    public static CategoryTotals ByCategory(
        IEnumerable<Transaction> transactions, DateOnly from, DateOnly to, TransactionType type)
    {
        var result = new Dictionary<Guid, decimal>();
        var uncategorized = 0m;
        foreach (var t in transactions.Where(t => t.Type == type && t.Date >= from && t.Date <= to))
        {
            if (t.CategoryId is { } id) result[id] = result.GetValueOrDefault(id) + t.AmountPln;
            else uncategorized += t.AmountPln;
        }
        return new CategoryTotals(result, uncategorized);
    }

    public static EventReport ForEvent(SpecialEvent e, IEnumerable<Transaction> all, IEnumerable<Category> categories, DateOnly today)
    {
        var list = all.Where(t => t.EventId == e.Id).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
        var expenses = list.Where(t => t.Type == TransactionType.Expense).ToList();
        var expense = expenses.Sum(t => t.AmountPln);
        var income = list.Where(t => t.Type == TransactionType.Income).Sum(t => t.AmountPln);

        var days = today < e.DateFrom ? 0 : (Min(today, e.DateTo).DayNumber - e.DateFrom.DayNumber) + 1;
        decimal? perDay = days > 0 ? Math.Round(expense / days, 2, MidpointRounding.AwayFromZero) : null;

        decimal? planned = e.PlannedBudgetPln is > 0 ? e.PlannedBudgetPln : null;
        decimal? used = planned is { } p ? expense / p : null;
        decimal? remaining = planned is { } p2 ? p2 - expense : null;

        var byId = categories.ToDictionary(c => c.Id);
        var slices = expenses
            .GroupBy(t => RootOf(t.CategoryId, byId))
            .Select(g =>
            {
                var cat = g.Key is { } id && byId.TryGetValue(id, out var c) ? c : null;
                var amount = g.Sum(t => t.AmountPln);
                return new CategorySlice(g.Key, cat?.Name ?? "", cat?.Color ?? "", amount, expense == 0 ? 0 : amount / expense);
            })
            .OrderByDescending(s => s.AmountPln)
            .ToList();

        return new EventReport(e, list.Count, expense, income, days, perDay, planned, used, remaining, slices, list);
    }

    private static Guid? RootOf(Guid? id, IReadOnlyDictionary<Guid, Category> byId)
    {
        if (id is not { } current || !byId.ContainsKey(current)) return null;
        for (var guard = 0; guard < 16 && byId[current].ParentId is { } parent && byId.ContainsKey(parent); guard++)
            current = parent;
        return current;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}
