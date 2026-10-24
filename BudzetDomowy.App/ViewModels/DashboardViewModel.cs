using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;

namespace BudzetDomowy.App.ViewModels;

/// <param name="Id">Category the slice stands for; null for entries without a category.</param>
public sealed record PieSlice(Guid? Id, string Name, string Color, string AmountText, string ShareText, bool CanDrill);

/// <param name="Status">Ok, Warning or Over (drives the colour of the bar).</param>
public sealed record BudgetBar(string Path, string Text, double Fraction, string PercentText, string Status);

public sealed record RecentRow(DateTime Date, string Place, string Category, string CategoryColor, string AmountText, bool IsIncome);

public sealed partial class DashboardViewModel : ObservableObject, IPeriodNavigable
{
    private readonly BudgetCore _core;
    private Guid? _drillRoot;

    [ObservableProperty] private DateOnly _month = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));
    [ObservableProperty] private string _monthTitle = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _expenseText = "";
    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private bool _balanceNegative;
    [ObservableProperty] private string _incomeChange = "";
    [ObservableProperty] private string _expenseChange = "";
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _hasSlices;
    [ObservableProperty] private IReadOnlyList<PieSlice> _slices = [];
    [ObservableProperty] private IEnumerable<ISeries> _pieSeries = [];
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
        UiPreferences.Instance.OnAppearanceChanged(this, self => self.Reload());
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnMonthChanged(DateOnly value)
    {
        _drillRoot = null;
        Reload();
    }

    public void PreviousPeriod() => Month = Month.AddMonths(-1);
    public void NextPeriod() => Month = Month.AddMonths(1);

    [RelayCommand] private void PreviousMonth() => Month = Month.AddMonths(-1);
    [RelayCommand] private void NextMonth() => Month = Month.AddMonths(1);
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

    public void Reload()
    {
        var all = _core.Reader.GetTransactions();
        var cats = _core.Reader.GetCategories();
        var today = DateOnly.FromDateTime(DateTime.Today);
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);

        var title = Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        MonthTitle = char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..];

        var cmp = PeriodReports.CompareMonth(all, Month);
        IncomeText = Money(cmp.Current.Income);
        ExpenseText = Money(cmp.Current.Expense);
        BalanceText = Money(cmp.Current.Balance);
        BalanceNegative = cmp.Current.Balance < 0;
        IncomeChange = ChangeText(cmp.Income);
        ExpenseChange = ChangeText(cmp.Expense);

        BuildBreakdown(all, cats);
        BuildBudgets(all, cats);

        Recent = all.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).Take(8).Select(t =>
        {
            var cat = t.CategoryId is { } id ? cats.FirstOrDefault(c => c.Id == id) : null;
            return new RecentRow(t.Date.ToDateTime(TimeOnly.MinValue), t.Place.Length > 0 ? t.Place : t.Note,
                cat?.Name ?? "", cat?.Color ?? "",
                UiPreferences.Instance.SignOf(t.Type == TransactionType.Income) + Money(t.AmountPln), t.Type == TransactionType.Income);
        }).ToList();

        BuildEvent(all, cats, today);
    }

    private static string ChangeText(Change c)
    {
        if (c.Percent is not { } p) return c.Current == 0 ? "" : Res.Get("DashNoPrevious");
        if (Math.Round(p * 100m) == 0) return Res.Get("DashNoChange");
        var arrow = p >= 0 ? "▲" : "▼";
        return Res.Format("DashChange", arrow, Math.Abs(p * 100m).ToString("0", CultureInfo.CurrentCulture));
    }

    private void BuildBreakdown(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        var from = Month;
        var to = PeriodReports.MonthEnd(Month);
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
        var items = level.Select((b, i) => new
        {
            b,
            Name = b.IsOwn ? Res.Format("DashOwnPart", b.Name) : b.Name.Length == 0 ? uncategorized : b.Name,
            Color = ChartFactory.ColorOrDefault(b.Color, i),
        }).ToList();

        Slices = items.Select(x => new PieSlice(x.b.CategoryId, x.Name, x.Color, Money(x.b.Amount),
            (x.b.Share * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%", _drillRoot is null && x.b.Children.Count > 0)).ToList();
        PieSeries = ChartFactory.Donut(items.Select(x => (x.Name, x.Color, x.b.Amount)));
        HasSlices = items.Count > 0;
        CanGoBack = drilled is not null;
        ChartTitle = drilled is null ? Res.Get("DashExpensesByCategory") : Res.Format("DashInCategory", drilled.Name);
    }

    private void BuildBudgets(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        string Money(decimal v) => v.ToString("C0", CultureInfo.CurrentCulture);
        var lines = BudgetMath.Progress(Month, _core.Reader.GetBudgets(), cats, all);
        BudgetBars = lines.Select(l => new BudgetBar(l.Path,
            Res.Format("DashBudgetOf", Money(l.Spent), Money(l.Limit)),
            (double)Math.Min(1m, l.Fraction),
            (l.Fraction * 100m).ToString("0", CultureInfo.CurrentCulture) + "%",
            l.Status.ToString())).ToList();
        HasBudgets = lines.Count > 0;
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
