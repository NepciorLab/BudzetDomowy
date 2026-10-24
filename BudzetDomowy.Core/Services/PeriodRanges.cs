using System.Globalization;

namespace BudzetDomowy.Core.Services;

public enum PeriodKind { Week, Month, Year }

/// <summary>Date ranges for browsing by week (Monday to Sunday), month or year, and stepping to the previous or next one.</summary>
public static class PeriodRanges
{
    public static (DateOnly From, DateOnly To) Range(PeriodKind kind, DateOnly anchor) => kind switch
    {
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
        PeriodKind.Week => anchor.AddDays(7 * steps),
        PeriodKind.Month => anchor.AddMonths(steps),
        _ => anchor.AddYears(steps),
    };

    public static int IsoWeek(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));
}
