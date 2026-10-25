using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Corrections;

/// <summary>One day compared before and after a correction: the change that was expected and the change that happened.</summary>
public sealed record DayCheck(DateOnly Date, DaySum Before, DaySum After, DaySum Expected)
{
    public DaySum Actual => After - Before;

    public bool Ok => Actual == Expected;
}

/// <summary>
/// The check made after a correction: every day of the data is compared before and after, and the change of each day must be exactly
/// the one the plan promised (nothing for days the plan does not touch).
/// </summary>
public sealed record VerificationReport(IReadOnlyList<DayCheck> Days, int DaysChecked, DaySum TotalBefore, DaySum TotalAfter, DaySum TotalExpected)
{
    /// <summary>Every day (month) that was compared, also the ones that did not change; <see cref="Days"/> lists only the interesting ones.</summary>
    public IReadOnlyList<DayCheck> All { get; init; } = Days;

    /// <summary>The days where the change is not the expected one.</summary>
    public IReadOnlyList<DayCheck> Problems => Days.Where(d => !d.Ok).ToList();

    /// <summary>The days (months) whose sums of income or of expenses differ before and after (a changed number of entries alone is not a difference of the sums).</summary>
    public IReadOnlyList<DayCheck> Differences => Days.Where(d => d.Actual.Income != 0 || d.Actual.Expense != 0).ToList();

    /// <summary>The change of the totals, everything together.</summary>
    public DaySum TotalChange => TotalAfter - TotalBefore;

    /// <summary>What the totals changed against the promise, summed over the days (months) where they did not change as promised.</summary>
    public DaySum TotalUnexpected => Problems.Aggregate(DaySum.Empty, (sum, d) => sum + (d.Actual - d.Expected));

    public bool AllOk => Problems.Count == 0 && TotalAfter - TotalBefore == TotalExpected;

    /// <summary>The days that were expected to change or did change.</summary>
    public int ChangedDays => Days.Count;
}

public static class Verifier
{
    /// <summary>
    /// Compares the totals per day before and after. <paramref name="expected"/> holds the promised change of the days that should change;
    /// every other day is expected to stay exactly as it was.
    /// </summary>
    public static VerificationReport Compare(IReadOnlyDictionary<DateOnly, DaySum> before, IReadOnlyDictionary<DateOnly, DaySum> after,
        IReadOnlyDictionary<DateOnly, DaySum> expected)
    {
        var dates = before.Keys.Concat(after.Keys).Concat(expected.Keys).Distinct().OrderBy(d => d).ToList();
        var days = new List<DayCheck>();
        var all = new List<DayCheck>();
        foreach (var date in dates)
        {
            var check = new DayCheck(date, before.GetValueOrDefault(date, DaySum.Empty), after.GetValueOrDefault(date, DaySum.Empty),
                expected.GetValueOrDefault(date, DaySum.Empty));
            all.Add(check);
            if (!check.Actual.IsEmpty || !check.Expected.IsEmpty) days.Add(check);
        }

        return new VerificationReport(days, dates.Count, Sum(before.Values), Sum(after.Values), Sum(expected.Values)) { All = all };
    }

    private static DaySum Sum(IEnumerable<DaySum> values) => values.Aggregate(DaySum.Empty, (a, b) => a + b);
}
