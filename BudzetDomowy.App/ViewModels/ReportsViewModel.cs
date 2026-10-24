using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;

namespace BudzetDomowy.App.ViewModels;

/// <param name="Trend">Good, Bad or Neutral: whether the change is welcome (colours the delta).</param>
public sealed record ComparisonRow(string Label, string Current, string Previous, string Delta, string Percent, string Trend);

public sealed record BreakdownNode(string Name, string Color, string AmountText, string ShareText, IReadOnlyList<BreakdownNode> Children);

public sealed record EventRow(string Name, string Dates, string Spent, string PerDay, string Plan);

public enum ReportTab { Overview, Compare, Places }

public sealed partial class ReportsViewModel : ObservableObject, IPeriodNavigable
{
    private readonly BudgetCore _core;
    private readonly ExportActions _exports;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOverview)), NotifyPropertyChangedFor(nameof(IsCompare)),
        NotifyPropertyChangedFor(nameof(IsPlaces)), NotifyPropertyChangedFor(nameof(ShowPeriod))] private ReportTab _tab;

    public bool IsOverview => Tab == ReportTab.Overview;
    public bool IsCompare => Tab == ReportTab.Compare;
    public bool IsPlaces => Tab == ReportTab.Places;
    public bool ShowPeriod => Tab != ReportTab.Compare;

    public CompareViewModel Compare { get; }
    public PlacesViewModel Places { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMonth))] private bool _isYear;
    [ObservableProperty] private DateOnly _anchor = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _previousTitle = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _expenseText = "";
    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private IReadOnlyList<ComparisonRow> _comparison = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TrendIsColumns))] private bool _trendLines;
    public bool TrendIsColumns => !TrendLines;
    [ObservableProperty] private IEnumerable<ISeries> _trendSeries = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _trendXAxes = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _trendYAxes = [];
    [ObservableProperty] private string _trendTitle = "";

    [ObservableProperty] private IEnumerable<ISeries> _pieSeries = [];
    [ObservableProperty] private IReadOnlyList<PieSlice> _topCategories = [];
    [ObservableProperty] private IReadOnlyList<BreakdownNode> _breakdown = [];
    [ObservableProperty] private bool _hasBreakdown;

    // averages over any range
    [ObservableProperty] private DateTime? _avgFrom;
    [ObservableProperty] private DateTime? _avgTo;
    [ObservableProperty] private bool _avgProrate = true;
    [ObservableProperty] private string _avgDays = "";
    [ObservableProperty] private string _avgPerDay = "";
    [ObservableProperty] private string _avgPerWeek = "";
    [ObservableProperty] private string _avgPerMonth = "";
    [ObservableProperty] private string _avgTotal = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AvgIsIncome))] private bool _avgIsExpense = true;
    [ObservableProperty] private IReadOnlyList<CategoryPick> _avgChoices = [];
    [ObservableProperty] private CategoryPick? _avgCategory;

    public bool AvgIsIncome => !AvgIsExpense;
    [ObservableProperty] private string _avgMethod = "";

    [ObservableProperty] private IReadOnlyList<EventRow> _events = [];
    [ObservableProperty] private bool _hasEvents;

    public bool IsMonth => !IsYear;

    public LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint LegendTextPaint => ChartFactory.TextPaint();

    public ReportsViewModel(BudgetCore core, DataHub hub, ExportActions exports)
    {
        _core = core;
        _exports = exports;
        Compare = new CompareViewModel(core);
        Places = new PlacesViewModel(core);
        var today = DateTime.Today;
        _avgFrom = new DateTime(today.Year, 1, 1);
        _avgTo = today;
        hub.Changed += (_, _) =>
        {
            Reload();
            Compare.Refresh();
            Places.Reload();
        };
        Reload();
    }

    partial void OnIsYearChanged(bool value) => Reload();
    partial void OnTrendLinesChanged(bool value) => Reload();

    [RelayCommand]
    private void SetTrendStyle(string? key) => TrendLines = key == "Lines";
    partial void OnAnchorChanged(DateOnly value) => Reload();
    partial void OnAvgFromChanged(DateTime? value) => ReloadAverages();
    partial void OnAvgToChanged(DateTime? value) => ReloadAverages();
    partial void OnAvgProrateChanged(bool value) => ReloadAverages();
    partial void OnAvgIsExpenseChanged(bool value) => RebuildAvgChoices();
    partial void OnAvgCategoryChanged(CategoryPick? value) => ReloadAverages();

    [RelayCommand]
    private void SetTab(string? key)
    {
        if (Enum.TryParse<ReportTab>(key, out var tab)) Tab = tab;
    }

    [RelayCommand]
    private void SetAvgType(string? key) => AvgIsExpense = key != "Income";

    private void RebuildAvgChoices()
    {
        var type = AvgIsExpense ? TransactionType.Expense : TransactionType.Income;
        var previous = AvgCategory?.Id;
        AvgChoices = CategoryPick.Build(_core.Reader.GetCategories(), type, Res.Get("CompareAllCategories"));
        AvgCategory = AvgChoices.FirstOrDefault(c => c.Id == previous) ?? AvgChoices[0];
        ReloadAverages();
    }

    public void PreviousPeriod() => Anchor = IsYear ? Anchor.AddYears(-1) : Anchor.AddMonths(-1);
    public void NextPeriod() => Anchor = IsYear ? Anchor.AddYears(1) : Anchor.AddMonths(1);

    [RelayCommand]
    private void Previous() => PreviousPeriod();

    [RelayCommand]
    private void Next() => NextPeriod();

    [RelayCommand]
    private void Export() => _exports.Report(IsYear ? ReportScope.Year : ReportScope.Month, Anchor);

    [RelayCommand]
    private void Current() => Anchor = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));

    private static string MonthName(DateOnly d)
    {
        var s = d.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
    }

    public void Reload()
    {
        var all = _core.Reader.GetTransactions();
        var cats = _core.Reader.GetCategories();
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);

        DateOnly from, to;
        PeriodComparison cmp;
        if (IsYear)
        {
            from = new DateOnly(Anchor.Year, 1, 1);
            to = new DateOnly(Anchor.Year, 12, 31);
            cmp = PeriodReports.CompareYear(all, Anchor.Year);
            Title = Anchor.Year.ToString(CultureInfo.CurrentCulture);
            PreviousTitle = (Anchor.Year - 1).ToString(CultureInfo.CurrentCulture);
        }
        else
        {
            from = Anchor;
            to = PeriodReports.MonthEnd(Anchor);
            cmp = PeriodReports.CompareMonth(all, Anchor);
            Title = MonthName(Anchor);
            PreviousTitle = MonthName(Anchor.AddMonths(-1));
        }

        IncomeText = Money(cmp.Current.Income);
        ExpenseText = Money(cmp.Current.Expense);
        BalanceText = Money(cmp.Current.Balance);
        Comparison =
        [
            Row(Res.Get("ReportIncome"), cmp.Income, higherIsBetter: true),
            Row(Res.Get("ReportExpenses"), cmp.Expense, higherIsBetter: false),
            Row(Res.Get("ReportBalance"), cmp.Balance, higherIsBetter: true),
        ];

        // trend: the year's twelve months, or the twelve months ending with the chosen one
        var firstMonth = IsYear ? new DateOnly(Anchor.Year, 1, 1) : Anchor.AddMonths(-11);
        var lastMonth = IsYear ? new DateOnly(Anchor.Year, 12, 1) : Anchor;
        var points = PeriodReports.MonthlyTrend(all, firstMonth, lastMonth);
        TrendSeries = ChartFactory.IncomeExpenseColumns(points, Res.Get("ReportIncome"), Res.Get("ReportExpenses"), TrendLines);
        TrendXAxes = ChartFactory.MonthAxes(points, IsYear ? "MMM" : "MMM yy");
        TrendYAxes = ChartFactory.MoneyAxes();
        TrendTitle = IsYear ? Res.Format("ReportTrendYear", Anchor.Year) : Res.Get("ReportTrend12");

        // categories
        var roots = PeriodReports.Breakdown(all, cats, from, to, TransactionType.Expense);
        var uncategorized = Res.Get("NoCategory");
        string NameOf(BreakdownItem b) => b.IsOwn ? Res.Format("DashOwnPart", b.Name) : b.Name.Length == 0 ? uncategorized : b.Name;
        var top = roots.Take(5).Select((b, i) => new { b, Name = NameOf(b), Color = ChartFactory.ColorOrDefault(b.Color, i) }).ToList();
        TopCategories = top.Select(x => new PieSlice(x.b.CategoryId, x.Name, x.Color, Money(x.b.Amount),
            (x.b.Share * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%", false)).ToList();
        PieSeries = ChartFactory.Donut(roots.Select((b, i) => (NameOf(b), ChartFactory.ColorOrDefault(b.Color, i), b.Amount)), 60);
        Breakdown = roots.Select((b, i) => ToNode(b, i, NameOf, Money)).ToList();
        HasBreakdown = roots.Count > 0;

        Places.SetPeriod(from, to);
        if (AvgChoices.Count == 0) RebuildAvgChoices(); else ReloadAverages();
        BuildEvents(all, cats);
    }

    private BreakdownNode ToNode(BreakdownItem b, int index, Func<BreakdownItem, string> nameOf, Func<decimal, string> money) =>
        new(nameOf(b), ChartFactory.ColorOrDefault(b.Color, index), money(b.Amount),
            (b.Share * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%",
            b.Children.Select((c, i) => ToNode(c, i + index + 1, nameOf, money)).ToList());

    private static ComparisonRow Row(string label, Change c, bool higherIsBetter)
    {
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        var delta = c.Delta;
        var sign = delta > 0 ? "+" : delta < 0 ? "−" : "";
        var percent = c.Percent is { } p ? (p >= 0 ? "+" : "−") + Math.Abs(p * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%" : "—";
        var trend = delta == 0 ? "Neutral" : (delta > 0) == higherIsBetter ? "Good" : "Bad";
        return new ComparisonRow(label, Money(c.Current), Money(c.Previous), sign + Money(Math.Abs(delta)), percent, trend);
    }

    private void BuildEvents(IReadOnlyList<Transaction> all, IReadOnlyList<Category> cats)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        Events = _core.Reader.GetSpecialEvents().OrderByDescending(e => e.DateFrom).Select(e =>
        {
            var r = Reports.ForEvent(e, all, cats, today);
            return new EventRow(e.Name,
                $"{e.DateFrom.ToString("d", CultureInfo.CurrentCulture)} – {e.DateTo.ToString("d", CultureInfo.CurrentCulture)}",
                Money(r.ExpensePln), r.ExpensePerDay is { } d ? Money(d) : "—",
                r.PlannedPln is { } p ? $"{Money(p)} ({((r.PlannedUsedFraction ?? 0) * 100m).ToString("0", CultureInfo.CurrentCulture)}%)" : "—");
        }).ToList();
        HasEvents = Events.Count > 0;
    }

    public void ReloadAverages()
    {
        if (AvgFrom is not { } f || AvgTo is not { } t) return;
        var mode = AvgProrate ? IncompleteMonthMode.Prorate : IncompleteMonthMode.ExcludeIncomplete;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var type = AvgIsExpense ? TransactionType.Expense : TransactionType.Income;
        var a = ComparisonReports.Average(_core.Reader.GetTransactions(), _core.Reader.GetCategories(),
            DateOnly.FromDateTime(f), DateOnly.FromDateTime(t), today, mode, type, AvgCategory?.Id);
        string Money(decimal? v) => v is { } m ? m.ToString("C2", CultureInfo.CurrentCulture) : "—";

        AvgDays = a.Days.ToString(CultureInfo.CurrentCulture);
        AvgPerDay = Money(a.PerDay);
        AvgPerWeek = Money(a.PerWeek);
        AvgPerMonth = Money(a.PerMonth);
        AvgTotal = Money(a.Total);
        var months = a.Months.ToString("0.##", CultureInfo.CurrentCulture);
        AvgMethod = Res.Get("AvgMethodBase") + " " + (mode == IncompleteMonthMode.Prorate
            ? Res.Format("AvgMethodProrate", a.Days, months)
            : Res.Format("AvgMethodExclude", months));
    }
}
