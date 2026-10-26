using System.Globalization;

namespace BudzetDomowy.Core.Services;

public enum PeriodKind { Week, Month, Year, Day }

/// <summary>Date ranges for browsing by week (Monday to Sunday), month or year, and stepping to the previous or next one.</summary>
public static class PeriodRanges
{
    public static (DateOnly From, DateOnly To) Range(PeriodKind kind, DateOnly anchor) => kind switch
    {
        PeriodKind.Day => (anchor, anchor),
        PeriodKind.Week => WeekOf(anchor),
        PeriodKind.Month => (new DateOnly(anchor.Year, anchor.Month, 1), PeriodReports.MonthEnd(anchor)),
        _ => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 31)),
    };

    public static (DateOnly From, DateOnly To) WeekOf(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;   // Monday = 0
        var monday = date.AddDays(-offset);
        return (monday, monday.AddDays(6));
    }

    /// <summary>The anchor moved by whole periods; a month or year keeps the day of month where it exists.</summary>
    public static DateOnly Shift(PeriodKind kind, DateOnly anchor, int steps) => kind switch
    {
        PeriodKind.Day => anchor.AddDays(steps),
        PeriodKind.Week => anchor.AddDays(7 * steps),
        PeriodKind.Month => anchor.AddMonths(steps),
        _ => anchor.AddYears(steps),
    };

    public static int IsoWeek(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));
}

/// <summary>
/// Where the arrows of a period lead: only to periods that have entries (a period without any in between is jumped over), plus the
/// current period once it has begun, so the newest month can be looked at before the first entry of it is made.
/// </summary>
public static class PeriodNavigation
{
    /// <summary>A date inside the period to move to, or null when there is nowhere to go in that direction.</summary>
    public static DateOnly? Target(PeriodKind kind, DateOnly anchor, int direction, Storage.LocalReader reader, DateOnly today)
    {
        var (from, to) = PeriodRanges.Range(kind, anchor);
        if (direction < 0) return reader.LatestEntryDayBefore(from);

        var next = reader.EarliestEntryDayAfter(to);
        var current = PeriodRanges.Range(kind, today).From > to ? today : (DateOnly?)null;       // the present period, when it lies ahead
        if (next is null) return current;
        return current is { } c && c < next.Value ? c : next;
    }

    public static bool CanMove(PeriodKind kind, DateOnly anchor, int direction, Storage.LocalReader reader, DateOnly today) =>
        Target(kind, anchor, direction, reader, today) is not null;
}
