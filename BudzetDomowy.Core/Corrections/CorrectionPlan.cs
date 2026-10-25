using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Corrections;

public enum ChangeKind
{
    /// <summary>An existing transaction gets new values (same id).</summary>
    Update,

    /// <summary>A new transaction is written.</summary>
    Add,
}

/// <summary>One transaction the plan will write: what it is now (<see cref="Before"/>, null for a new one) and what it will be.</summary>
public sealed record PlannedChange(ChangeKind Kind, Transaction? Before, Transaction After);

public enum SkipReason
{
    /// <summary>The amount would not stay above zero.</summary>
    NotPositive,

    /// <summary>The part to split off is not smaller than the whole amount.</summary>
    PartTooBig,

    /// <summary>No part was given, or it is zero or negative.</summary>
    NoPart,

    /// <summary>The transaction has several lines; changing its amount would leave them inconsistent.</summary>
    SeveralLines,
}

public sealed record SkippedItem(Transaction Transaction, SkipReason Reason);

/// <summary>Income and expenses (in zloty, as the reports count them) and the number of transactions: the totals of one day, or a change of them.</summary>
public sealed record DaySum(decimal Income, decimal Expense, int Count)
{
    public static readonly DaySum Empty = new(0m, 0m, 0);

    public static DaySum operator +(DaySum a, DaySum b) => new(a.Income + b.Income, a.Expense + b.Expense, a.Count + b.Count);

    public static DaySum operator -(DaySum a, DaySum b) => new(a.Income - b.Income, a.Expense - b.Expense, a.Count - b.Count);

    public bool IsEmpty => this == Empty;

    public static DaySum Of(Transaction t) => t.Type == TransactionType.Income ? new DaySum(t.AmountPln, 0m, 1) : new DaySum(0m, t.AmountPln, 1);
}

public static class DaySnapshot
{
    /// <summary>Totals per day of everything given.</summary>
    public static Dictionary<DateOnly, DaySum> Build(IEnumerable<Transaction> transactions)
    {
        var days = new Dictionary<DateOnly, DaySum>();
        foreach (var t in transactions)
            days[t.Date] = days.GetValueOrDefault(t.Date, DaySum.Empty) + DaySum.Of(t);
        return days;
    }
}

/// <summary>What a correction is going to write, worked out in advance so that it can be looked at (and checked afterwards).</summary>
public sealed record CorrectionPlan(string Title, IReadOnlyList<PlannedChange> Changes, IReadOnlyList<SkippedItem> Skipped)
{
    public bool IsEmpty => Changes.Count == 0;

    public int Updates => Changes.Count(c => c.Kind == ChangeKind.Update);

    public int Adds => Changes.Count(c => c.Kind == ChangeKind.Add);

    /// <summary>How the totals of each day must change when exactly this plan is applied; days that stay the same are not listed.</summary>
    public IReadOnlyDictionary<DateOnly, DaySum> ExpectedDeltas()
    {
        var deltas = new Dictionary<DateOnly, DaySum>();
        void Add(DateOnly date, DaySum change) => deltas[date] = deltas.GetValueOrDefault(date, DaySum.Empty) + change;

        foreach (var c in Changes)
        {
            if (c.Before is { } before) Add(before.Date, DaySum.Empty - DaySum.Of(before));
            Add(c.After.Date, DaySum.Of(c.After));
        }
        return deltas.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
