namespace BudzetDomowy.Core.Domain;

/// <summary>A category prepared for pickers: display text with the parent path and nesting depth.</summary>
public sealed record CategoryOption(Category Category, string DisplayName, int Depth)
{
    public Guid Id => Category.Id;
    public override string ToString() => DisplayName;
}

public static class CategoryHelpers
{
    public const string PathSeparator = " › ";

    /// <summary>
    /// Flattens categories depth-first (parents before their children, siblings by SortOrder then name, or alphabetically for pick lists).
    /// Orphans (parent missing) are listed as roots.
    /// </summary>
    public static IReadOnlyList<CategoryOption> Flatten(IEnumerable<Category> categories, Func<Category, bool>? include = null, bool alphabetical = false)
    {
        var all = categories.ToList();
        var ids = all.Select(c => c.Id).ToHashSet();
        var byParent = all.ToLookup(c => c.ParentId is { } p && ids.Contains(p) ? p : Guid.Empty);
        var result = new List<CategoryOption>();

        void Walk(Guid parent, string prefix, int depth)
        {
            var siblings = alphabetical
                ? byParent[parent].OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                : byParent[parent].OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var c in siblings)
            {
                var name = prefix.Length == 0 ? c.Name : prefix + PathSeparator + c.Name;
                if (include is null || include(c)) result.Add(new CategoryOption(c, name, depth));
                Walk(c.Id, name, depth + 1);
            }
        }

        Walk(Guid.Empty, "", 0);
        return result;
    }

    /// <summary>Categories usable for new entries of the given transaction type (not archived, matching type).</summary>
    public static IReadOnlyList<CategoryOption> ForEntry(IEnumerable<Category> categories, TransactionType type) =>
        Flatten(categories, c => !c.IsArchived && Matches(c.Type, type), alphabetical: true);

    public static bool Matches(CategoryType categoryType, TransactionType type) =>
        categoryType == CategoryType.Both
        || (categoryType == CategoryType.Expense && type == TransactionType.Expense)
        || (categoryType == CategoryType.Income && type == TransactionType.Income);

    public static string PathOf(Guid? categoryId, IReadOnlyDictionary<Guid, Category> byId)
    {
        if (categoryId is not { } id || !byId.TryGetValue(id, out var c)) return "";
        var parts = new List<string> { c.Name };
        var guard = 0;
        while (c.ParentId is { } p && byId.TryGetValue(p, out var parent) && guard++ < 16)
        {
            parts.Insert(0, parent.Name);
            c = parent;
        }
        return string.Join(PathSeparator, parts);
    }

    /// <summary>Ids of the category and all of its descendants.</summary>
    public static HashSet<Guid> WithDescendants(Guid id, IEnumerable<Category> categories)
    {
        var list = categories.ToList();
        var set = new HashSet<Guid> { id };
        bool grew;
        do
        {
            grew = false;
            foreach (var c in list)
                if (c.ParentId is { } p && set.Contains(p) && set.Add(c.Id)) grew = true;
        } while (grew);
        return set;
    }
}

public static class SpecialEvents
{
    /// <summary>The event whose date range contains <paramref name="date"/> (the one that started last, if several).</summary>
    public static SpecialEvent? ActiveOn(IEnumerable<SpecialEvent> events, DateOnly date) =>
        events.Where(e => e.DateFrom <= date && date <= e.DateTo).OrderByDescending(e => e.DateFrom).FirstOrDefault();
}
