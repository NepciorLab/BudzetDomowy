using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;

namespace BudzetDomowy.App.ViewModels;

/// <param name="Id">Null stands for "all" (every category, or the top level).</param>
public sealed record CategoryPick(Guid? Id, string Name)
{
    public override string ToString() => Name;

    /// <summary>"All" first, then the categories that fit the type (<paramref name="onlyParents"/>: only those with subcategories).</summary>
    public static IReadOnlyList<CategoryPick> Build(IReadOnlyList<Category> categories, TransactionType type, string allLabel, bool onlyParents = false)
    {
        var parents = categories.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).ToHashSet();
        var list = new List<CategoryPick> { new(null, allLabel) };
        list.AddRange(CategoryHelpers.Flatten(categories, c => CategoryHelpers.Matches(c.Type, type) && (!onlyParents || parents.Contains(c.Id)), alphabetical: true)
            .Select(o => new CategoryPick(o.Id, o.DisplayName)));
        return list;
    }
}

/// <summary>A year or a month that can be ticked for the comparison.</summary>
public sealed partial class PickItem : ObservableObject
{
    private readonly Action _changed;

    public string Label { get; }
    public object Key { get; }

    [ObservableProperty] private bool _isSelected;

    public PickItem(string label, object key, bool selected, Action changed)
    {
        Label = label;
        Key = key;
        _isSelected = selected;   // the backing field: no notification while building
        _changed = changed;
    }

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public enum CompareKind { Years, Months }

public enum CompareStyle { Columns, Lines, Averages }

public sealed record CompareTotal(string Name, string Color, string Total, string Average);

/// <summary>Years side by side (month by month) or months side by side (category by category), for expenses or income.</summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private bool _building;
    private readonly HashSet<int> _selectedYears = [];
    private readonly HashSet<DateOnly> _selectedMonths = [];
    private bool _yearsInitialized;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsYears)), NotifyPropertyChangedFor(nameof(IsMonths))]
    private CompareKind _kind = CompareKind.Years;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIncome))] private bool _isExpense = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsColumns)), NotifyPropertyChangedFor(nameof(IsLines)), NotifyPropertyChangedFor(nameof(IsAverages))]
    private CompareStyle _style = CompareStyle.Columns;

    public bool IsColumns => Style == CompareStyle.Columns;
    public bool IsLines => Style == CompareStyle.Lines;
    public bool IsAverages => Style == CompareStyle.Averages;

    [ObservableProperty] private IReadOnlyList<CategoryPick> _choices = [];
    [ObservableProperty] private CategoryPick? _category;
    [ObservableProperty] private IReadOnlyList<PickItem> _picks = [];

    [ObservableProperty] private IEnumerable<ISeries> _series = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _xAxes = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _yAxes = [];
    [ObservableProperty] private IReadOnlyList<CompareTotal> _totals = [];
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _title = "";

    public bool IsYears => Kind == CompareKind.Years;
    public bool IsMonths => Kind == CompareKind.Months;
    public bool IsIncome => !IsExpense;
    public string PickLabel => Res.Get(IsYears ? "CompareYearsPick" : "CompareMonthsPick");
    public string CategoryLabel => Res.Get(IsYears ? "CompareCategoryYears" : "CompareCategoryMonths");

    public LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint LegendTextPaint => ChartFactory.TextPaint();

    public CompareViewModel(BudgetCore core)
    {
        _core = core;
        RebuildAll();
    }

    private TransactionType Type => IsExpense ? TransactionType.Expense : TransactionType.Income;

    partial void OnKindChanged(CompareKind value)
    {
        if (value == CompareKind.Months && Style == CompareStyle.Averages)
        {
            // running averages are per year; the redraw happens once the lists are rebuilt
            _building = true;
            Style = CompareStyle.Columns;
            _building = false;
        }
        RebuildAll();
    }
    partial void OnIsExpenseChanged(bool value) => RebuildAll();
    partial void OnStyleChanged(CompareStyle value)
    {
        if (!_building) Reload();
    }

    [RelayCommand]
    private void SetChartStyle(string? key)
    {
        if (Enum.TryParse<CompareStyle>(key, out var style) && (style != CompareStyle.Averages || IsYears)) Style = style;
    }

    partial void OnCategoryChanged(CategoryPick? value)
    {
        if (!_building) Reload();
    }

    [RelayCommand]
    private void SetKind(string? key)
    {
        if (Enum.TryParse<CompareKind>(key, out var kind)) Kind = kind;
    }

    [RelayCommand]
    private void SetType(string? key) => IsExpense = key != "Income";

    /// <summary>Refreshes the lists after the data changed (a new year may have appeared) and redraws.</summary>
    public void Refresh() => RebuildAll();

    private void RebuildAll()
    {
        _building = true;
        try
        {
            var cats = _core.Reader.GetCategories();
            var all = _core.Reader.GetTransactions();
            var previous = Category?.Id;

            Choices = CategoryPick.Build(cats, Type, Res.Get(IsYears ? "CompareAllCategories" : "CompareTopLevel"), onlyParents: IsMonths);
            Category = Choices.FirstOrDefault(c => c.Id == previous) ?? Choices[0];

            var today = DateOnly.FromDateTime(DateTime.Today);
            if (IsYears)
            {
                var years = all.Select(t => t.Date.Year).Append(today.Year).Distinct().OrderByDescending(y => y).ToList();
                if (!_yearsInitialized)
                {
                    foreach (var y in years.Where(y => y >= today.Year - 1).Take(2)) _selectedYears.Add(y);
                    _yearsInitialized = true;
                }
                Picks = years.Select(y => new PickItem(y.ToString(CultureInfo.CurrentCulture), y, _selectedYears.Contains(y), OnPicked)).ToList();
            }
            else
            {
                var thisMonth = new DateOnly(today.Year, today.Month, 1);
                if (_selectedMonths.Count == 0)
                    for (var i = 0; i < 3; i++) _selectedMonths.Add(thisMonth.AddMonths(-i));
                Picks = Enumerable.Range(0, 24).Select(i => thisMonth.AddMonths(-i))
                    .Select(m => new PickItem(MonthText(m), m, _selectedMonths.Contains(m), OnPicked)).ToList();
            }
        }
        finally
        {
            _building = false;
        }

        OnPropertyChanged(nameof(PickLabel));
        OnPropertyChanged(nameof(CategoryLabel));
        Reload();
    }

    private void OnPicked()
    {
        if (_building) return;
        foreach (var p in Picks)
        {
            if (p.Key is int y) { if (p.IsSelected) _selectedYears.Add(y); else _selectedYears.Remove(y); }
            else if (p.Key is DateOnly m) { if (p.IsSelected) _selectedMonths.Add(m); else _selectedMonths.Remove(m); }
        }
        Reload();
    }

    private static string MonthText(DateOnly m) => m.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public void Reload()
    {
        if (_building) return;
        var all = _core.Reader.GetTransactions();
        var cats = _core.Reader.GetCategories();
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        var typeName = Res.Get(IsExpense ? "CompareExpenses" : "CompareIncome");
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (IsYears)
        {
            var years = Picks.Where(p => p.IsSelected).Select(p => (int)p.Key).ToList();
            var data = ComparisonReports.YearsByMonth(all, cats, years, Type, Category?.Id);
            Series = IsAverages ? ChartFactory.YearAverages(data, today) : ChartFactory.YearColumns(data, IsLines);
            XAxes = ChartFactory.LabelAxes(CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames.Take(12));
            Totals = data.Select((y, i) =>
            {
                // a running year is averaged over the months so far
                var months = y.Year == today.Year ? today.Month : y.Year > today.Year ? 1 : 12;
                return new CompareTotal(y.Year.ToString(CultureInfo.CurrentCulture), ChartFactory.Palette[i % ChartFactory.Palette.Length],
                    Money(y.Total), Res.Format("CompareAverage", Money(y.Total / months)));
            }).ToList();
            HasData = data.Any(y => y.Total != 0);
            Title = Res.Format(IsAverages ? "CompareTitleAverages" : "CompareTitleYears", typeName, Category?.Name ?? "");
        }
        else
        {
            var months = Picks.Where(p => p.IsSelected).Select(p => (DateOnly)p.Key).ToList();
            var cmp = ComparisonReports.MonthsByCategory(all, cats, months, Type, Category?.Id);
            var labels = cmp.Labels.Select(l => l == ComparisonReports.OthersLabel ? Res.Get("CompareOthers") : l.Length == 0 ? Res.Get("NoCategory") : l).ToList();
            Series = ChartFactory.MonthColumns(cmp.Series, MonthText, IsLines);
            XAxes = ChartFactory.LabelAxes(labels, rotation: labels.Count > 5 ? 25 : 0);
            Totals = cmp.Series.Select((m, i) => new CompareTotal(MonthText(m.Month), ChartFactory.Palette[i % ChartFactory.Palette.Length],
                Money(m.Total), "")).ToList();
            HasData = cmp.Series.Any(m => m.Total != 0);
            Title = Res.Format("CompareTitleMonths", typeName, Category?.Name ?? "");
        }
        YAxes = ChartFactory.MoneyAxes();
    }
}
