using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Services;

/// <summary>
/// A dictionary built from what was booked before: for each place (shop, service) the categories it was booked under, most often used
/// first. The first line of a new entry gets the most frequent category of the typed place; the next lines get the next most frequent
/// ones that the entry does not use yet. Nothing is stored apart from the transactions themselves, so the dictionary is as old as the
/// history (an imported workbook included) and the same on every computer.
/// </summary>
public sealed class PlaceCategoryDictionary
{
    public const int MinPrefixLength = 3;

    private readonly Dictionary<string, List<Use>> _byPlace;

    private sealed record Use(Guid CategoryId, int Count, DateOnly Last);

    private PlaceCategoryDictionary(Dictionary<string, List<Use>> byPlace) => _byPlace = byPlace;

    public int Places => _byPlace.Count;

    public static string Normalize(string? place) => CategoryRuleService.Normalize(place);

    /// <summary>Counts the categories per place for transactions of the given type; transactions without a place or a category are skipped.</summary>
    public static PlaceCategoryDictionary Build(IEnumerable<Transaction> transactions, TransactionType type)
    {
        var counts = new Dictionary<string, Dictionary<Guid, (int Count, DateOnly Last)>>();
        foreach (var t in transactions)
        {
            if (t.Type != type || t.CategoryId is not { } category) continue;
            var key = Normalize(t.Place);
            if (key.Length == 0) continue;

            if (!counts.TryGetValue(key, out var inner)) counts[key] = inner = [];
            inner.TryGetValue(category, out var current);
            inner[category] = (current.Count + 1, t.Date > current.Last ? t.Date : current.Last);
        }

        return new PlaceCategoryDictionary(counts.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(c => new Use(c.Key, c.Value.Count, c.Value.Last))
                .OrderByDescending(u => u.Count).ThenByDescending(u => u.Last).ThenBy(u => u.CategoryId).ToList()));
    }

    /// <summary>
    /// The categories for a typed place, most frequent first. An exact place wins; otherwise the longest known place that the text
    /// contains ("MPK Kraków" finds "MPK"); otherwise places that start with what has been typed so far (three letters or more).
    /// </summary>
    public IReadOnlyList<Guid> Suggest(string? place)
    {
        var text = Normalize(place);
        if (text.Length == 0) return [];
        if (_byPlace.TryGetValue(text, out var exact)) return exact.Select(u => u.CategoryId).ToList();

        var contained = _byPlace.Keys.Where(k => k.Length >= MinPrefixLength && text.Contains(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length).FirstOrDefault();
        if (contained is not null) return _byPlace[contained].Select(u => u.CategoryId).ToList();

        if (text.Length < MinPrefixLength) return [];
        var merged = _byPlace.Where(kv => kv.Key.StartsWith(text, StringComparison.Ordinal)).SelectMany(kv => kv.Value)
            .GroupBy(u => u.CategoryId)
            .Select(g => new Use(g.Key, g.Sum(u => u.Count), g.Max(u => u.Last)))
            .OrderByDescending(u => u.Count).ThenByDescending(u => u.Last).ThenBy(u => u.CategoryId);
        return merged.Select(u => u.CategoryId).ToList();
    }

    /// <summary>The most frequent suggestion that is not in <paramref name="alreadyUsed"/> (null when there is none).</summary>
    public Guid? SuggestNext(string? place, IReadOnlyCollection<Guid> alreadyUsed) =>
        Suggest(place).Where(c => !alreadyUsed.Contains(c)).Select(c => (Guid?)c).FirstOrDefault();
}
