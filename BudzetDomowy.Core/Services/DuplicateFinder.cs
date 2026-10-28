using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Services;

/// <param name="LineIndex">Which line of the new entry has the twin.</param>
/// <param name="Existing">The entry that is already there.</param>
public sealed record DuplicateMatch(int LineIndex, Transaction Existing);

/// <summary>
/// Finds the entries that are already there when a new one is about to be saved: the same day, kind (expense or income), currency, place,
/// category and amount. A new entry with several lines is compared line by line, and an existing entry that has several lines is
/// compared by each of them.
/// </summary>
public static class DuplicateFinder
{
    public static IReadOnlyList<DuplicateMatch> Find(IEnumerable<Transaction> existing, DateOnly date, TransactionType type, string currency, string? place,
        IReadOnlyList<(Guid? CategoryId, decimal Amount)> lines)
    {
        var sameDay = existing.Where(t => t.Date == date && t.Type == type && SameCurrency(t.CurrencyCode, currency) && SamePlace(t.Place, place)).ToList();
        var found = new List<DuplicateMatch>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (category, amount) = lines[i];
            var twin = sameDay.FirstOrDefault(t => HasLine(t, category, amount));
            if (twin is not null) found.Add(new DuplicateMatch(i, twin));
        }
        return found;
    }

    /// <summary>The entry has this category with this amount: as a whole (one line) or as one of its lines.</summary>
    public static bool HasLine(Transaction t, Guid? categoryId, decimal amount) =>
        t.Lines.Count > 1
            ? t.Lines.Any(l => l.CategoryId == categoryId && l.Amount == amount)
            : t.CategoryId == categoryId && t.Amount == amount;

    private static bool SameCurrency(string a, string b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? "PLN" : a.Trim(), string.IsNullOrWhiteSpace(b) ? "PLN" : b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePlace(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase);
}
