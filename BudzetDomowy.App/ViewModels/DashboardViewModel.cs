using System.Globalization;
using System.Collections.ObjectModel;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

/// <param name="Id">Category the slice stands for; null for entries without a category.</param>
public sealed record PieSlice(Guid? Id, string Name, string Color, string AmountText, string ShareText, bool CanDrill);

/// <param name="Status">Ok, Warning or Over (drives the colour of the bar).</param>
public sealed record BudgetBar(string Path, string Text, double Fraction, string PercentText, string Status);

/// <param name="HasData">There are entries in that month.</param>
/// <param name="IsFuture">A month that has not begun (it cannot be chosen).</param>
public sealed record PickerMonth(int Number, string Name, bool IsCurrent, bool HasData, bool IsFuture);

public sealed record RecentRow(DateTime Date, string Place, string Category, string CategoryColor, string AmountText, bool IsIncome);

public sealed partial class DashboardViewModel : ObservableObject, IPeriodNavigable, IRefreshOnShow
{
    private readonly BudgetCore _core;
    private readonly MorphingBreakdown _breakdown = new();
    private Guid? _drillRoot;

    [ObservableProperty] private DateOnly _month = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));
    /// <summary>The whole year of <see cref="Month"/> instead of the month alone (the same screen, a longer period).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMonthMode)), NotifyPropertyChangedFor(nameof(PreviousTip)), NotifyPropertyChangedFor(nameof(NextTip)),
     NotifyPropertyChangedFor(nameof(PickTip)), NotifyPropertyChangedFor(nameof(BudgetsTitle)), NotifyPropertyChangedFor(nameof(PickerTitle)),
     NotifyPropertyChangedFor(nameof(CanPickerGoBack))] private bool _isYear;
    [ObservableProperty] private string _monthTitle = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _expenseText = "";
    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private bool _balanceNegative;
    [ObservableProperty] private string _incomeChange = "";
    [ObservableProperty] private string _expenseChange = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChangeTip))] private string _changeTip = "";
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _hasSlices;
    [ObservableProperty] private IReadOnlyList<PieSlice> _slices = [];
    [ObservableProperty] private ObservableCollection<ChartItem> _chartItems;
    [ObservableProperty] private IReadOnlyList<BudgetBar> _budgetBars = [];
    [ObservableProperty] private bool _hasBudgets;
    [ObservableProperty] private IReadOnlyList<RecentRow> _recent = [];

    // active event
    [ObservableProperty] private bool _hasActiveEvent;
    [ObservableProperty] private string _eventName = "";
    [ObservableProperty] private string _eventDates = "";
    [ObservableProperty] private string _eventSpent = "";
    [ObservableProperty] private string _eventPerDay = "";
    [ObservableProperty] private string _eventPlan = "";
    [ObservableProperty] private double _eventFraction;
    [ObservableProperty] private bool _eventHasPlan;
    [ObservableProperty] private bool _eventOver;

    public DashboardViewModel(BudgetCore core, DataHub hub)
    {
        _core = core;
        _chartItems = _breakdown.Items;                // the same collection for good: another month moves the ring and the bars instead of drawing them again
        UiPreferences.Instance.OnAppearanceChanged(this, self => self.Reload());
        UiPreferences.Instance.PropertyChanged += (_, e) =>
        {
            // the number of rows only changes sizes: the legend and the area follow at once, nothing is read or rebuilt while the slider moves
            if (e.PropertyName != nameof(UiPreferences.ChartRows)) return;
            OnPropertyChanged(nameof(LegendHeight));
            OnPropertyChanged(nameof(ChartAreaHeight));
        };
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnMonthChanged(DateOnly value)
    {
        _drillRoot = null;
        Reload();
    }

    public bool IsMonthMode => !IsYear;

    public string PreviousTip => Res.Get(IsYear ? "DashPrevYear" : "DashPrevMonth");
    public string NextTip => Res.Get(IsYear ? "DashNextYear" : "DashNextMonth");
    public string PickTip => Res.Get(IsYear ? "DashPickYear" : "DashPickMonth");
    public string BudgetsTitle => Res.Get(IsYear ? "DashBudgetsYear" : "DashBudgets");

    partial void OnIsYearChanged(bool value)
    {
        _drillRoot = null;
        if (IsPickerOpen) IsPickerOpen = false;
        Reload();
    }

    /// <param name="mode">"Month" or "Year" (text because it comes from XAML).</param>
    [RelayCommand]
    private void SetMode(string? mode) => IsYear = mode == "Year";

    /// <summary>The month of the chosen year the screen stays on: the same month number, but never one that has not begun.</summary>
    private static DateOnly InYear(int year, DateOnly like)
    {
        var month = new DateOnly(Math.Clamp(year, 1, 9999), like.Month, 1);
        var now = BudgetMath.MonthStart(Today);
        return month > now ? now : month;
    }

    private PeriodKind Kind => IsYear ? PeriodKind.Year : PeriodKind.Month;

    // ----- choosing the month and the year -----

    private const int FirstPickerYear = 2000;
    private HashSet<(int Year, int Month)> _monthsWithData = [];
    private DateTime _pickerClosedAt = DateTime.MinValue;

    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private int _pickerYear = DateTime.Today.Year;
    [ObservableProperty] private IReadOnlyList<PickerMonth> _pickerMonths = [];

    public bool HasChangeTip => ChangeTip.Length > 0;

    // ----- the area of the charts: its size does not depend on the month, so nothing jumps while the charts move -----

    public const double LegendRowHeight = 27;
    private const double MinChartAreaHeight = 296;                          // what the bars need: the plot, the names and room for a scroll bar

    /// <summary>The legend has room for this many categories; with more of them it scrolls.</summary>
    public double LegendHeight => Math.Round(UiPreferences.Instance.ChartRows) * LegendRowHeight;

    public double ChartAreaHeight => Math.Max(LegendHeight, MinChartAreaHeight);

    public bool CanPickerGoForward => PickerYear < Today.Year;

    public bool CanPickerGoBack => PickerYear - (IsYear ? YearsShown - 1 : 0) > FirstPickerYear;

    /// <summary>The list of years shows this many years, the last one being <see cref="PickerYear"/>.</summary>
    private const int YearsShown = 12;

    public string PickerTitle => IsYear ? $"{PickerYear - YearsShown + 1}–{PickerYear}" : PickerYear.ToString(CultureInfo.CurrentCulture);

    partial void OnIsPickerOpenChanged(bool value)
    {
        if (value) PickerYear = IsYear ? Math.Min(Today.Year, Math.Max(Month.Year + YearsShown / 2, FirstPickerYear + YearsShown - 1)) : Month.Year;
        else _pickerClosedAt = DateTime.UtcNow;
        BuildPickerMonths();
    }

    partial void OnPickerYearChanged(int value)
    {
        OnPropertyChanged(nameof(CanPickerGoForward));
        OnPropertyChanged(nameof(CanPickerGoBack));
        OnPropertyChanged(nameof(PickerTitle));
        BuildPickerMonths();
    }

    private void BuildPickerMonths()
    {
        var culture = CultureInfo.CurrentCulture;
        var today = Today;
        if (IsYear)
        {
            // twelve years ending with PickerYear; Number is the year itself
            PickerMonths = Enumerable.Range(PickerYear - YearsShown + 1, YearsShown)
                .Select(y => new PickerMonth(y, y.ToString(culture), y == Month.Year, _monthsWithData.Any(m => m.Year == y), y > today.Year)).ToList();
            return;
        }

        PickerMonths = Enumerable.Range(1, 12).Select(n =>
        {
            var name = culture.DateTimeFormat.GetAbbreviatedMonthName(n).TrimEnd('.');
            var future = PickerYear > today.Year || (PickerYear == today.Year && n > today.Month);
            return new PickerMonth(n, char.ToUpper(name[0], culture) + name[1..], PickerYear == Month.Year && n == Month.Month,
                _monthsWithData.Contains((PickerYear, n)), future);
        }).ToList();
    }

    /// <summary>Opens or closes the list of months. A click on the title closes it by itself (the popup loses the focus), so the same click must not open it again.</summary>
    [RelayCommand]
    private void TogglePicker()
    {
        if (IsPickerOpen) IsPickerOpen = false;
        else if ((DateTime.UtcNow - _pickerClosedAt).TotalMilliseconds > 250) IsPickerOpen = true;
    }

    /// <param name="years">Number of years to move by (negative = back), as text because it comes from XAML.</param>
    [RelayCommand]
    private void ShiftPickerYear(string? years)
    {
        if (!int.TryParse(years, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by)) return;
        PickerYear = Math.Clamp(PickerYear + by, FirstPickerYear + (IsYear ? YearsShown - 1 : 0), Today.Year);
    }

    [RelayCommand]
    private void PickMonth(int number)
    {
        if (IsYear)                                                                       // in the list of years the number is the year
        {
            if (number < FirstPickerYear || number > Today.Year) return;
            IsPickerOpen = false;
            Month = InYear(number, Month);
            return;
        }

        if (number is < 1 or > 12) return;
        var month = new DateOnly(PickerYear, number, 1);
        if (month > BudgetMath.MonthStart(Today)) return;
        IsPickerOpen = false;
        Month = month;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>The arrows lead only to months with entries (and to the present month once it has begun).</summary>
    public bool CanGoPrevious => PeriodNavigation.CanMove(Kind, Month, -1, _core.Reader, Today);

    public bool CanGoNext => PeriodNavigation.CanMove(Kind, Month, +1, _core.Reader, Today);

    private void Step(int direction)
    {
        if (PeriodNavigation.Target(Kind, Month, direction, _core.Reader, Today) is { } date)
            Month = IsYear ? InYear(date.Year, Month) : BudgetMath.MonthStart(date);
    }

    private void RefreshArrows()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void PreviousPeriod() => Step(-1);
    public void NextPeriod() => Step(+1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))] private void PreviousMonth() => Step(-1);
    [RelayCommand(CanExecute = nameof(CanGoNext))] private void NextMonth() => Step(+1);
    [RelayCommand] private void ThisMonth() => Month = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void DrillInto(PieSlice? slice)
    {
        if (slice is not { CanDrill: true, Id: { } id }) return;
        _drillRoot = id;
        Reload();
    }

    [RelayCommand]
    private void Back()
    {
        _drillRoot = null;
        Reload();
    }

    public void RefreshOnShow() => Reload();

    public void Reload()
    {
        var all = _core.Reader.GetTransactions();
        var cats = _core.Reader.GetCategories();
        var today = DateOnly.FromDateTime(DateTime.Today);
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);

        var title = IsYear ? Month.Year.ToString(CultureInfo.CurrentCulture) : Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        MonthTitle = char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..];

        var cmp = IsYear ? PeriodReports.CompareYear(all, Month.Year) : PeriodReports.CompareMonth(all, Month);
        IncomeText = Money(cmp.Current.Income);
        ExpenseText = Money(cmp.Current.Expense);
        BalanceText = Money(cmp.Current.Balance);
        BalanceNegative = cmp.Current.Balance < 0;

        // a month (or a year) that is still running is compared with the same days of the one before, not with all of it
        var running = IsYear ? Month.Year == today.Year : Month == BudgetMath.MonthStart(today);
        var tip = "";
        var compared = running ? (IsYear ? SamePeriodOfYear(all, today, out tip) : SamePeriod(all, today, out tip)) : cmp;
        ChangeTip = tip;
        IncomeChange = ChangeText(compared.Income, running);
        ExpenseChange = ChangeText(compared.Expense, running);
        _monthsWithData = all.Select(t => (t.Date.Year, t.Date.Month)).ToHashSet();
        BuildPickerMonths();

        BuildBreakdown(all, cats);
        BuildBudgets(all, cats);
        RefreshArrows();
        PreviousMonthCommand.NotifyCanExecuteChanged();
        NextMonthCommand.NotifyCanExecuteChanged();

        Recent = all.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).Take(8).Select(t =>
        {
            var cat = t.CategoryId is { } id ? cats.FirstOrDefault(c => c.Id == id) : null;
            return new RecentRow(t.Date.ToDateTime(TimeOnly.MinValue), t.Place.Length > 0 ? t.Place : t.Note,
                cat?.Name ?? "", cat?.Color ?? "",
                UiPreferences.Instance.SignOf(t.Type == TransactionType.Income) + Money(t.AmountPln), t.Type == TransactionType.Income);
        }).ToList();

        BuildEvent(all, cats, today);
    }

    /// <summary>The days of the running month so far against the same days of the previous month (all of it when that was shorter).</summary>
    private static PeriodComparison SamePeriod(IReadOnlyList<Transaction> all, DateOnly today, out string tip)
    {
        var start = BudgetMath.MonthStart(today);
        var previous = start.AddMonths(-1);
        var previousEnd = new DateOnly(previous.Year, previous.Month, Math.Min(today.Day, DateTime.DaysInMonth(previous.Year, previous.Month)));
        var names = CultureInfo.CurrentCulture.DateTimeFormat.MonthGenitiveNames;
        tip = Res.Format("DashChangeTip", $"1–{today.Day} {names[start.Month - 1]}", $"1–{previousEnd.Day} {names[previous.Month - 1]}");
        return new PeriodComparison(PeriodReports.Summary(all, start, today), PeriodReports.Summary(all, previous, previousEnd));
    }

    /// <summary>The days of the running year so far against the same days of the year before.</summary>
    private static PeriodComparison SamePeriodOfYear(IReadOnlyList<Transaction> all, DateOnly today, out string tip)
    {
        var start = new DateOnly(today.Year, 1, 1);
        var previous = start.AddYears(-1);
        var previousEnd = previous.AddDays(today.DayNumber - start.DayNumber);       // the same day of the year before
        var names = CultureInfo.CurrentCulture.DateTimeFormat.MonthGenitiveNames;
        string Range(DateOnly from, DateOnly to) => $"{from.Day} {names[from.Month - 1]}–{to.Day} {names[to.Month - 1]} {to.Year}";
        tip = Res.Format("DashChangeTip", Range(start, today), Range(previous, previousEnd));
        return new PeriodComparison(PeriodReports.Summary(all, start, today), PeriodReports.Summary(all, previous, previousEnd));
    }

    private static string ChangeText(Change c, bool period)
    {
        if (c.Percent is not { } p) return c.Current == 0 ? "" : Res.Get(period ? "DashNoPreviousPeriod" : "DashNoPrevious");
        if (Math.Round(p * 100m) == 0) return Res.Get(period ? "DashNoChangePeriod" : "DashNoChange");
        var arrow = p >= 0 ? "▲" : "▼";
        return Res.Format(period ? "DashChangePeriod" : "DashChange", arrow, Math.Abs(p * 100m).ToString("0", CultureInfo.CurrentCulture));
    }

    private void BuildBreakdown(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        var from = IsYear ? new DateOnly(Month.Year, 1, 1) : Month;
        var to = IsYear ? new DateOnly(Month.Year, 12, 31) : PeriodReports.MonthEnd(Month);
        var roots = PeriodReports.Breakdown(all, cats, from, to, TransactionType.Expense);
        var level = roots;
        BreakdownItem? drilled = null;
        if (_drillRoot is { } id)
        {
            drilled = roots.FirstOrDefault(r => r.CategoryId == id);
            if (drilled is null) _drillRoot = null; else level = drilled.Children;
        }

        var uncategorized = Res.Get("NoCategory");
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        var colors = ChartFactory.Distinguish(level.Select((b, i) => ChartFactory.ColorOrDefault(b.Color, i)).ToList());
        var items = level.Select((b, i) => new
        {
            b,
            Name = b.IsOwn ? Res.Format("DashOwnPart", b.Name) : b.Name.Length == 0 ? uncategorized : b.Name,
            Color = colors[i],
        }).ToList();

        Slices = items.Select(x => new PieSlice(x.b.CategoryId, x.Name, x.Color, Money(x.b.Amount),
            (x.b.Share * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%", _drillRoot is null && x.b.Children.Count > 0)).ToList();
        _breakdown.Update(items.Select(x => ($"{x.b.CategoryId}|{x.Name}", x.Name, x.Color, x.b.Amount)));
        OnPropertyChanged(nameof(LegendHeight));
        OnPropertyChanged(nameof(ChartAreaHeight));
        HasSlices = items.Count > 0;
        CanGoBack = drilled is not null;
        ChartTitle = drilled is null ? Res.Get("DashExpensesByCategory") : Res.Format("DashInCategory", drilled.Name);
    }

    private void BuildBudgets(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        string Money(decimal v) => v.ToString("C0", CultureInfo.CurrentCulture);
        var lines = IsYear ? YearProgress(all, cats) : BudgetMath.Progress(Month, _core.Reader.GetBudgets(), cats, all);
        BudgetBars = lines.Select(l => new BudgetBar(l.Path,
            Res.Format("DashBudgetOf", Money(l.Spent), Money(l.Limit)),
            (double)Math.Min(1m, l.Fraction),
            (l.Fraction * 100m).ToString("0", CultureInfo.CurrentCulture) + "%",
            l.Status.ToString())).ToList();
        HasBudgets = lines.Count > 0;
    }

    /// <summary>
    /// A year against its limits: for each category the monthly limits of the months in which it had one are added up (up to the running
    /// month in the present year) and so is what was spent in those months.
    /// </summary>
    private IReadOnlyList<BudgetLine> YearProgress(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        var budgets = _core.Reader.GetBudgets();
        var last = Month.Year == Today.Year ? Today.Month : 12;
        var total = new Dictionary<Guid, (BudgetLine First, decimal Limit, decimal Spent)>();
        for (var m = 1; m <= last; m++)
            foreach (var line in BudgetMath.Progress(new DateOnly(Month.Year, m, 1), budgets, cats, all))
            {
                total[line.Category.Id] = total.TryGetValue(line.Category.Id, out var so)
                    ? (so.First, so.Limit + line.Limit, so.Spent + line.Spent)
                    : (line, line.Limit, line.Spent);
            }

        return total.Values
            .Select(t => new BudgetLine(t.First.Category, t.First.Path, t.Limit, t.Spent, t.Spent / t.Limit, BudgetMath.StatusOf(t.Spent / t.Limit)))
            .OrderByDescending(l => l.Fraction).ThenBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void BuildEvent(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats, DateOnly today)
    {
        var active = SpecialEvents.ActiveOn(_core.Reader.GetSpecialEvents(), today);
        HasActiveEvent = active is not null;
        if (active is null) return;

        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        var r = Reports.ForEvent(active, all, cats, today);
        EventName = active.Name;
        EventDates = $"{active.DateFrom.ToString("d", CultureInfo.CurrentCulture)} – {active.DateTo.ToString("d", CultureInfo.CurrentCulture)}";
        EventSpent = Money(r.ExpensePln);
        EventPerDay = r.ExpensePerDay is { } d ? Res.Format("DashPerDay", Money(d)) : "";
        EventHasPlan = r.PlannedPln is not null;
        EventOver = r.RemainingPln is < 0;
        EventFraction = (double)Math.Min(1m, r.PlannedUsedFraction ?? 0m);
        EventPlan = r.PlannedPln is { } plan ? Res.Format("DashPlanOf", Money(r.ExpensePln), Money(plan)) : "";
    }
}
