using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Core.Corrections;

/// <summary>One transaction to split and how much goes to the new part.</summary>
public sealed record SplitRequest(Transaction Original, decimal PartAmount);

/// <summary>An entry to add every month between two dates.</summary>
public sealed record MonthlyAddSpec(TransactionType Type, decimal Amount, Guid? CategoryId, string Place, string Note, DateOnly From, DateOnly To, int DayOfMonth,
    Guid? EventId = null);

/// <summary>
/// Works out corrections without writing anything: change the amounts of chosen transactions by the same sum, split transactions in two,
/// add an entry every month. The result is a plan (see <see cref="CorrectionPlan"/>) that can be shown, applied and checked.
/// </summary>
public static class CorrectionPlanner
{
    /// <summary>The most entries one "add every month" correction may create.</summary>
    public const int MaxAdded = 1200;

    /// <summary>Every chosen transaction gets <paramref name="delta"/> added to its amount (a negative one makes it smaller).</summary>
    public static CorrectionPlan ShiftAmounts(IEnumerable<Transaction> selected, decimal delta, string title)
    {
        var changes = new List<PlannedChange>();
        var skipped = new List<SkippedItem>();
        if (delta != 0)
        {
            foreach (var t in selected)
            {
                if (t.Lines.Count > 1) { skipped.Add(new SkippedItem(t, SkipReason.SeveralLines)); continue; }
                var amount = t.Amount + delta;
                if (amount <= 0) { skipped.Add(new SkippedItem(t, SkipReason.NotPositive)); continue; }
                changes.Add(new PlannedChange(ChangeKind.Update, t, TransactionService.Normalize(t with { Amount = amount })));
            }
        }
        return new CorrectionPlan(title, changes, skipped);
    }

    /// <summary>
    /// Each transaction is split in two: the original keeps the rest (and its category), and a new one takes the part, in the same
    /// currency and on the same day, with <paramref name="partCategoryId"/> as its category.
    /// </summary>
    public static CorrectionPlan Split(IEnumerable<SplitRequest> requests, Guid? partCategoryId, string deviceId, DateTimeOffset now, string title)
    {
        var changes = new List<PlannedChange>();
        var skipped = new List<SkippedItem>();
        foreach (var (t, part) in requests.Select(r => (r.Original, r.PartAmount)))
        {
            if (t.Lines.Count > 1) { skipped.Add(new SkippedItem(t, SkipReason.SeveralLines)); continue; }
            if (part <= 0) { skipped.Add(new SkippedItem(t, SkipReason.NoPart)); continue; }
            if (part >= t.Amount) { skipped.Add(new SkippedItem(t, SkipReason.PartTooBig)); continue; }

            var rest = TransactionService.Normalize(t with { Amount = t.Amount - part });
            var split = TransactionService.Create(t.Date, t.Type, part, partCategoryId, t.Note, t.EventId, t.Place, t.CurrencyCode, t.ExchangeRateToPln,
                t.ExchangeRateDate, t.ExchangeRateManual, Guid.NewGuid(), now, deviceId);
            changes.Add(new PlannedChange(ChangeKind.Update, t, rest));
            changes.Add(new PlannedChange(ChangeKind.Add, null, split));
        }
        return new CorrectionPlan(title, changes, skipped);
    }

    /// <summary>The dates of one entry a month from <paramref name="from"/> to <paramref name="to"/>: the given day of each month, or the last one in a shorter month.</summary>
    public static IReadOnlyList<DateOnly> MonthlyDates(DateOnly from, DateOnly to, int dayOfMonth)
    {
        var dates = new List<DateOnly>();
        if (to < from || dayOfMonth is < 1 or > 31) return dates;
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var date = new DateOnly(month.Year, month.Month, Math.Min(dayOfMonth, DateTime.DaysInMonth(month.Year, month.Month)));
            if (date >= from && date <= to) dates.Add(date);
        }
        return dates;
    }

    /// <summary>One new transaction (an expense or an income) in each month of the range.</summary>
    public static CorrectionPlan AddMonthly(MonthlyAddSpec spec, string deviceId, DateTimeOffset now, string title)
    {
        if (spec.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "Amount must be positive.");
        var dates = MonthlyDates(spec.From, spec.To, spec.DayOfMonth);
        if (dates.Count > MaxAdded) throw new ArgumentException($"At most {MaxAdded} entries can be added at once.", nameof(spec));

        var changes = dates.Select(d => new PlannedChange(ChangeKind.Add, null,
            TransactionService.Create(d, spec.Type, spec.Amount, spec.CategoryId, spec.Note, spec.EventId, spec.Place, "PLN", 1m, null, false,
                Guid.NewGuid(), now, deviceId))).ToList();
        return new CorrectionPlan(title, changes, []);
    }
}
