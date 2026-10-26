using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Sync;
using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Services;

/// <summary>A place offered while typing in the "Place" box, with the weight that orders the offers (higher first).</summary>
public sealed record PlaceSuggestion(string Name, int Weight, bool FromHistory, bool WeightChanged);

/// <summary>
/// The suggestions of the "Place" box. They are a small database of <see cref="PlaceHint"/>s: a place is suggested while it has a hint
/// that is not hidden. The database is filled by "import from entries" (places typed at least a chosen number of times), by hand, and
/// by itself: a place typed in a new expense is added. The weight of a place is the number of expenses with it unless the user typed
/// another one. As long as there is no hint at all (a program updated from an older version) every place typed so far is suggested.
/// Nothing is stored apart from the transactions and the hints, so the suggestions are the same on every computer.
/// </summary>
public sealed class PlaceSuggestionService
{
    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;

    public PlaceSuggestionService(EventRecorder recorder, LocalReader reader)
    {
        _recorder = recorder;
        _reader = reader;
    }

    public static string Normalize(string? place) => CategoryRuleService.Normalize(place);

    public static Guid HintId(string place) => DeterministicId.FromString("place-hint:" + Normalize(place));

    private static string Clean(string? place) => string.Join(' ', (place ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>All suggested places, best first: by weight, then the most recently typed, then by name.</summary>
    public IReadOnlyList<PlaceSuggestion> All(TransactionType type = TransactionType.Expense) => Build(_reader.GetTransactions(), _reader.GetPlaceHints(), type);

    private sealed record Usage(string Name, int Count, DateOnly Last);

    private static Dictionary<string, Usage> Usages(IEnumerable<Transaction> transactions, TransactionType type)
    {
        var spellings = new Dictionary<string, Dictionary<string, int>>();
        var last = new Dictionary<string, DateOnly>();
        var count = new Dictionary<string, int>();
        foreach (var t in transactions)
        {
            if (t.Type != type) continue;
            var key = Normalize(t.Place);
            if (key.Length == 0) continue;
            if (!spellings.TryGetValue(key, out var inner)) spellings[key] = inner = [];
            var spelling = Clean(t.Place);
            inner[spelling] = inner.GetValueOrDefault(spelling) + 1;
            count[key] = count.GetValueOrDefault(key) + 1;
            if (!last.TryGetValue(key, out var l) || t.Date > l) last[key] = t.Date;
        }

        return spellings.ToDictionary(kv => kv.Key, kv => new Usage(
            kv.Value.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key, count[kv.Key], last[kv.Key]));
    }

    /// <summary>The places that have a hint (all typed places while there is no hint), with their weights.</summary>
    public static IReadOnlyList<PlaceSuggestion> Build(IEnumerable<Transaction> transactions, IEnumerable<PlaceHint> hints, TransactionType type = TransactionType.Expense)
    {
        var usages = Usages(transactions, type);
        var byKey = hints.GroupBy(h => Normalize(h.Name)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First());
        var result = new List<(PlaceSuggestion Item, DateOnly Last)>();

        if (byKey.Count == 0)
        {
            foreach (var (key, u) in usages) result.Add((new PlaceSuggestion(u.Name, u.Count, true, false), u.Last));
        }
        else
        {
            foreach (var (key, hint) in byKey)
            {
                if (hint.Hidden) continue;
                var fromHistory = usages.TryGetValue(key, out var u);
                var weight = hint.Weight ?? (fromHistory ? u!.Count : 1);
                result.Add((new PlaceSuggestion(hint.Name.Trim(), weight, fromHistory, hint.Weight is not null), fromHistory ? u!.Last : DateOnly.MinValue));
            }
        }

        return result.OrderByDescending(r => r.Item.Weight).ThenByDescending(r => r.Last)
            .ThenBy(r => r.Item.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Item).ToList();
    }

    /// <summary>
    /// What to offer for the typed text, already in the order of the weights: the places that start with it come first, then those
    /// in which a word starts with it. A single offer that is exactly what was typed is not offered again.
    /// </summary>
    public static IReadOnlyList<PlaceSuggestion> Match(IReadOnlyList<PlaceSuggestion> all, string? typed, int max = 8)
    {
        var text = Normalize(typed);
        if (text.Length == 0) return [];
        var starts = new List<PlaceSuggestion>();
        var words = new List<PlaceSuggestion>();
        foreach (var s in all)
        {
            var name = Normalize(s.Name);
            if (name.StartsWith(text, StringComparison.Ordinal)) starts.Add(s);
            else if (name.Split(' ').Any(w => w.StartsWith(text, StringComparison.Ordinal))) words.Add(s);
        }

        var matches = starts.Concat(words).Take(max).ToList();
        return matches.Count == 1 && Normalize(matches[0].Name) == text ? [] : matches;
    }

    /// <summary>The places that start with the typed text (letters in any case), in the order of <paramref name="all"/>; the place that is exactly the text is left out.</summary>
    public static IReadOnlyList<PlaceSuggestion> StartingWith(IReadOnlyList<PlaceSuggestion> all, string? typed, int max = int.MaxValue)
    {
        var text = typed ?? "";
        if (text.Trim().Length == 0) return [];
        return all.Where(s => s.Name.Length > text.Length && s.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)).Take(max).ToList();
    }

    // ----- the database -----

    /// <summary>True once the database exists (it has at least one hint); before that every typed place is suggested.</summary>
    public bool IsInitialized => _reader.GetPlaceHints().Count > 0;

    /// <summary>Makes the places typed so far the content of the database, so that editing it does not make the other ones disappear.</summary>
    private void EnsureInitialized()
    {
        if (!IsInitialized) Import(1);
    }

    /// <summary>The number of places (typed in expenses at least <paramref name="minEntries"/> times) the import would add.</summary>
    public int CountImportable(int minEntries) => Importable(minEntries).Count;

    private List<Usage> Importable(int minEntries)
    {
        var known = _reader.GetPlaceHints().Select(h => Normalize(h.Name)).ToHashSet();
        return Usages(_reader.GetTransactions(), TransactionType.Expense)
            .Where(kv => kv.Value.Count >= Math.Max(1, minEntries) && !known.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    }

    /// <summary>Adds the places typed in expenses at least <paramref name="minEntries"/> times that are not in the database yet (a removed place is not brought back). Returns how many were added.</summary>
    public int Import(int minEntries)
    {
        var add = Importable(minEntries);
        foreach (var u in add) _recorder.Save(new PlaceHint { Id = HintId(u.Name), Name = u.Name, Weight = null, Hidden = false });
        return add.Count;
    }

    /// <summary>A place typed in a new expense joins the database (unless the user removed it before, or the database does not exist yet).</summary>
    public void Learn(string? place)
    {
        var name = Clean(place);
        if (name.Length == 0 || !IsInitialized) return;
        var id = HintId(name);
        if (_reader.GetPlaceHints().Any(h => h.Id == id)) return;
        _recorder.Save(new PlaceHint { Id = id, Name = name, Weight = null, Hidden = false });
    }

    /// <summary>Adds a place by hand (or brings back a removed one) with a weight.</summary>
    public PlaceHint Add(string name, int weight)
    {
        name = Clean(name);
        if (name.Length == 0) throw new ArgumentException("A place needs a name.", nameof(name));
        EnsureInitialized();
        var hint = new PlaceHint { Id = HintId(name), Name = name, Weight = Math.Max(0, weight), Hidden = false };
        _recorder.Save(hint);
        return hint;
    }

    /// <summary>Gives the place another weight.</summary>
    public void SetWeight(string name, int weight)
    {
        EnsureInitialized();
        Add(name, weight);
    }

    /// <summary>Removes the place from the suggestions (a place typed later is suggested again only after "Add").</summary>
    public void Remove(string name) => Remove([name]);

    public void Remove(IEnumerable<string> names)
    {
        EnsureInitialized();
        foreach (var name in names.Select(Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            _recorder.Save(new PlaceHint { Id = HintId(name), Name = name, Weight = null, Hidden = true });
    }

    /// <summary>Drops the correction of a place: it is suggested by its number of uses again.</summary>
    public void ResetWeight(string name)
    {
        EnsureInitialized();
        _recorder.Save(new PlaceHint { Id = HintId(name), Name = Clean(name), Weight = null, Hidden = false });
    }
}
