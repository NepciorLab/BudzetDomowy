using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Services;

/// <param name="Months">Twelve values, January first.</param>
public sealed record YearSeries(int Year, decimal[] Months)
{
    public decimal Total => Months.Sum();
}

/// <param name="Values">One value per label, in the same order as <see cref="MonthCategoryComparison.Labels"/>.</param>
public sealed record MonthSeries(DateOnly Month, decimal[] Values, decimal Total);

/// <param name="CategoryIds">Category behind each label (null for entries without a category and for the "others" bucket).</param>
public sealed record MonthCategoryComparison(IReadOnlyList<string> Labels, IReadOnlyList<Guid?> CategoryIds, IReadOnlyList<MonthSeries> Series);

public sealed record PlaceTotal(string Name, int Count, decimal Amount, decimal Share);

public sealed record AverageOf(
    TransactionType Type, int Days, decimal Months, decimal Total,
    decimal? PerDay, decimal? PerWeek, decimal? PerMonth, IncompleteMonthMode Mode);

public static class ComparisonReports
{
    public const string OthersLabel = "…";

    /// <summary>
    /// Several years side by side, month by month (for charts "2025 against 2026"). With a category the sums cover it and all its
    /// subcategories; without one every transaction of the type counts.
    /// </summary>
    public static IReadOnlyList<YearSeries> YearsByMonth(
        IEnumerable<Transaction> all, IEnumerable<Category> categories, IEnumerable<int> years, TransactionType type, Guid? categoryId)
    {
        var ids = categoryId is { } c ? CategoryHelpers.WithDescendants(c, categories) : null;
        var wanted = years.Distinct().OrderBy(y => y).ToList();
        var result = wanted.ToDictionary(y => y, _ => new decimal[12]);

        foreach (var t in all)
        {
            if (t.Type != type || !result.TryGetValue(t.Date.Year, out var months)) continue;
            if (ids is not null && (t.CategoryId is not { } id || !ids.Contains(id))) continue;
            months[t.Date.Month - 1] += t.AmountPln;
        }
        return wanted.Select(y => new YearSeries(y, result[y])).ToList();
    }

    /// <summary>
    /// Several months side by side, category by category. Shows top-level categories, or the subcategories of
    /// <paramref name="parentCategoryId"/>. At most <paramref name="maxCategories"/> labels are kept (the biggest overall);
    /// the rest is added up under <see cref="OthersLabel"/>.
    /// </summary>
    public static MonthCategoryComparison MonthsByCategory(
        IEnumerable<Transaction> all, IEnumerable<Category> categories, IEnumerable<DateOnly> months, TransactionType type,
        Guid? parentCategoryId = null, int maxCategories = 10)
    {
        var list = all as IReadOnlyCollection<Transaction> ?? all.ToList();
        var cats = categories.ToList();
        var starts = months.Select(m => new DateOnly(m.Year, m.Month, 1)).Distinct().OrderBy(m => m).ToList();

        // category -> amount per month at the chosen level
        // Guid.Empty stands for "no category" (a dictionary cannot have a null key)
        var perMonth = new List<Dictionary<Guid, decimal>>();
        var names = new Dictionary<Guid, string>();
        foreach (var month in starts)
        {
            var level = LevelOf(PeriodReports.Breakdown(list, cats, month, PeriodReports.MonthEnd(month), type), parentCategoryId);
            var dict = new Dictionary<Guid, decimal>();
            foreach (var item in level)
            {
                // an "own" share of the parent keeps the parent's id so it forms one label for every month
                var key = item.CategoryId ?? Guid.Empty;
                dict[key] = dict.GetValueOrDefault(key) + item.Amount;
                names.TryAdd(key, item.Name);
            }
            perMonth.Add(dict);
        }

        var ranked = perMonth.SelectMany(d => d).GroupBy(kv => kv.Key).Select(g => (Id: g.Key, Total: g.Sum(kv => kv.Value)))
            .OrderByDescending(x => x.Total).ToList();
        var kept = ranked.Take(maxCategories).Select(x => x.Id).ToList();
        var hasOthers = ranked.Count > maxCategories;

        var labels = kept.Select(id => names[id]).ToList();
        var ids = kept.Select(k => k == Guid.Empty ? (Guid?)null : k).ToList();
        if (hasOthers) { labels.Add(OthersLabel); ids.Add(null); }

        var series = new List<MonthSeries>();
        for (var i = 0; i < starts.Count; i++)
        {
            var values = new decimal[labels.Count];
            for (var k = 0; k < kept.Count; k++) values[k] = perMonth[i].GetValueOrDefault(kept[k]);
            if (hasOthers) values[^1] = perMonth[i].Where(kv => !kept.Contains(kv.Key)).Sum(kv => kv.Value);
            series.Add(new MonthSeries(starts[i], values, perMonth[i].Values.Sum()));
        }
        return new MonthCategoryComparison(labels, ids, series);
    }

    private static IReadOnlyList<BreakdownItem> LevelOf(IReadOnlyList<BreakdownItem> roots, Guid? parentId)
    {
        if (parentId is not { } id) return roots;
        var node = Find(roots, id);
        return node?.Children ?? [];
    }

    private static BreakdownItem? Find(IEnumerable<BreakdownItem> items, Guid id)
    {
        foreach (var item in items)
        {
            if (item.CategoryId == id && !item.IsOwn) return item;
            if (Find(item.Children, id) is { } found) return found;
        }
        return null;
    }

    /// <summary>Totals per place in a period (places are matched ignoring case and spaces), biggest first.</summary>
    public static IReadOnlyList<PlaceTotal> ByPlace(IEnumerable<Transaction> all, DateOnly from, DateOnly to, TransactionType type, string noPlaceLabel = "")
    {
        var groups = all.Where(t => t.Type == type && t.Date >= from && t.Date <= to)
            .GroupBy(t => Normalize(t.Place))
            .Select(g =>
            {
                // show the spelling used most often
                var shown = g.Where(t => t.Place.Trim().Length > 0).GroupBy(t => t.Place.Trim()).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault()
                            ?? noPlaceLabel;
                return (Name: shown, Count: g.Count(), Amount: g.Sum(t => t.AmountPln));
            })
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var total = groups.Sum(x => x.Amount);
        return groups.Select(x => new PlaceTotal(x.Name, x.Count, x.Amount, total == 0 ? 0 : x.Amount / total)).ToList();
    }

    private static string Normalize(string place) => string.Join(' ', place.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>
    /// Average per day, week and month of one type, optionally for one category (with its subcategories) only —
    /// e.g. "groceries per week". Uses the same rules for the incomplete month as <see cref="PeriodReports.Average"/>.
    /// </summary>
    public static AverageOf Average(
        IEnumerable<Transaction> all, IEnumerable<Category> categories, DateOnly from, DateOnly to, DateOnly today,
        IncompleteMonthMode mode, TransactionType type, Guid? categoryId)
    {
        var ids = categoryId is { } c ? CategoryHelpers.WithDescendants(c, categories) : null;
        var filtered = ids is null ? all : all.Where(t => t.CategoryId is { } id && ids.Contains(id));
        var a = PeriodReports.Average(filtered, from, to, today, mode);

        var total = type == TransactionType.Expense ? a.Expense : a.Income;
        decimal? perDay = a.Days > 0 ? R(total / a.Days) : null;
        decimal? perWeek = a.Days > 0 ? R(total / ((decimal)a.Days / 7m)) : null;
        decimal? perMonth = a.Months > 0 ? R(total / a.Months) : null;
        return new AverageOf(type, a.Days, a.Months, total, perDay, perWeek, perMonth, mode);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
