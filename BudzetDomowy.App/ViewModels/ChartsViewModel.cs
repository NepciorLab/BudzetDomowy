using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;

namespace BudzetDomowy.App.ViewModels;

/// <summary>How much time one point of a line stands for.</summary>
public enum ChartStep { Week, Month, Year }

/// <summary>The points of a line chart: the start of each week, month or year of a range.</summary>
public static class ChartBuckets
{
    /// <summary>The first day of the week (Monday), month or year the date is in.</summary>
    public static DateOnly Start(DateOnly date, ChartStep step) => step switch
    {
        ChartStep.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        ChartStep.Month => new DateOnly(date.Year, date.Month, 1),
        _ => new DateOnly(date.Year, 1, 1),
    };

    public static DateOnly Next(DateOnly start, ChartStep step) => step switch
    {
        ChartStep.Week => start.AddDays(7),
        ChartStep.Month => start.AddMonths(1),
        _ => start.AddYears(1),
    };

    /// <summary>Every bucket from the one with <paramref name="from"/> to the one with <paramref name="to"/>.</summary>
    public static IReadOnlyList<DateOnly> Starts(DateOnly from, DateOnly to, ChartStep step)
    {
        var list = new List<DateOnly>();
        if (to < from) return list;
        for (var s = Start(from, step); s <= to; s = Next(s, step)) list.Add(s);
        return list;
    }

    public static string Label(DateOnly start, ChartStep step) => step switch
    {
        ChartStep.Week => start.ToString("dd.MM.yy", CultureInfo.CurrentCulture),
        ChartStep.Month => start.ToString("MMM yy", CultureInfo.CurrentCulture),
        _ => start.ToString("yyyy", CultureInfo.CurrentCulture),
    };
}

/// <summary>A category (or subcategory) picked for the chart: a small tag with the colour of its line and its total in the range.</summary>
public sealed partial class CategoryChip : ObservableObject
{
    public CategoryChip(Category category, string path, bool hasChildren, HashSet<Guid> ids)
    {
        Id = category.Id;
        Name = path;
        HasChildren = hasChildren;
        CategoryType = category.Type;
        Ids = ids;
    }

    public Guid Id { get; }

    /// <summary>The name with its parents, e.g. "Auto › Paliwo".</summary>
    public string Name { get; }

    public bool HasChildren { get; }
    public CategoryType CategoryType { get; }

    /// <summary>The category and all its subcategories: everything booked in them is on the line.</summary>
    internal HashSet<Guid> Ids { get; }

    [ObservableProperty] private string _color = "";
    [ObservableProperty] private string _totalText = "";

    internal bool Counts(Transaction t) => CategoryType switch
    {
        CategoryType.Income => t.Type == TransactionType.Income,
        CategoryType.Expense => t.Type == TransactionType.Expense,
        _ => true,
    };
}

/// <summary>
/// The "Wykresy" tab of the reports: a line for each chosen category (with its subcategories) over a range of dates that is set by two
/// date fields and by a bar with two handles that spans all the data. The categories are chosen from one box that filters while typing
/// and appear as tags, so there is no wall of check boxes; a quick choice puts the biggest ones in at once.
/// </summary>
public sealed partial class ChartsViewModel : ObservableObject
{
    private const int MaxChips = 15;

    private readonly BudgetCore _core;
    private List<Transaction> _all = [];
    private IReadOnlyList<Category> _categories = [];
    private Dictionary<Guid, Category> _byId = [];
    private bool _syncing;
    private bool _initialized;
    private DateOnly? _oldMin;
    private DateOnly? _oldMax;
    private DispatcherTimer? _timer;

    /// <summary>How long a dragged handle waits for the next move before the chart is redrawn; zero redraws at once (tests).</summary>
    public TimeSpan RebuildDelay { get; set; } = TimeSpan.FromMilliseconds(90);

    public ChartsViewModel(BudgetCore core)
    {
        _core = core;
        Chips.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChips));
            OnPropertyChanged(nameof(ShowHint));
        };
    }

    /// <summary>The data changed (or was never read): the next time the tab is shown it is read again.</summary>
    public bool Stale { get; set; } = true;

    /// <summary>Reads the data when it has not been read yet or has changed since.</summary>
    public void EnsureFresh()
    {
        if (Stale) Reload();
    }

    // ----- the range -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowHint)), NotifyPropertyChangedFor(nameof(NoData))] private bool _hasData;

    /// <summary>The first and the last day with an entry: the ends of the bar.</summary>
    [ObservableProperty] private DateOnly? _minDate;

    [ObservableProperty] private DateOnly? _maxDate;
    [ObservableProperty] private double _rangeMin;
    [ObservableProperty] private double _rangeMax = 1;
    [ObservableProperty] private double _lowerDay;
    [ObservableProperty] private double _upperDay = 1;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _rangeText = "";

    public bool NoData => !HasData;

    public DateOnly? From => FromDate is { } d ? DateOnly.FromDateTime(d) : null;

    public DateOnly? To => ToDate is { } d ? DateOnly.FromDateTime(d) : null;

    partial void OnFromDateChanged(DateTime? value)
    {
        if (_syncing || !HasData) return;
        SetRange(value is { } v ? DateOnly.FromDateTime(v) : MinDate!.Value, To ?? MaxDate!.Value, movedFrom: true);
    }

    partial void OnToDateChanged(DateTime? value)
    {
        if (_syncing || !HasData) return;
        SetRange(From ?? MinDate!.Value, value is { } v ? DateOnly.FromDateTime(v) : MaxDate!.Value, movedFrom: false);
    }

    partial void OnLowerDayChanged(double value)
    {
        if (_syncing || !HasData) return;
        SetRange(DateOnly.FromDayNumber((int)value), To ?? MaxDate!.Value, movedFrom: true);
    }

    partial void OnUpperDayChanged(double value)
    {
        if (_syncing || !HasData) return;
        SetRange(From ?? MinDate!.Value, DateOnly.FromDayNumber((int)value), movedFrom: false);
    }

    /// <summary>Puts the range in the date fields and on the bar at once; both stay inside the data and the start never passes the end.</summary>
    public void SetRange(DateOnly from, DateOnly to, bool movedFrom = true)
    {
        if (!HasData) return;
        from = from < MinDate!.Value ? MinDate.Value : from > MaxDate!.Value ? MaxDate.Value : from;
        to = to > MaxDate.Value ? MaxDate.Value : to < MinDate.Value ? MinDate.Value : to;
        if (from > to)
        {
            if (movedFrom) to = from;
            else from = to;
        }

        _syncing = true;
        try
        {
            FromDate = from.ToDateTime(TimeOnly.MinValue);
            ToDate = to.ToDateTime(TimeOnly.MinValue);
            LowerDay = from.DayNumber;
            UpperDay = to.DayNumber;
        }
        finally
        {
            _syncing = false;
        }
        UpdateRangeText();
        ScheduleRebuild();
    }

    private void UpdateRangeText()
    {
        if (From is not { } f || To is not { } t)
        {
            RangeText = "";
            return;
        }
        RangeText = Res.Format("ChartRangeText", (t.DayNumber - f.DayNumber + 1).ToString("N0", CultureInfo.CurrentCulture));
    }

    // ----- the step -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWeek)), NotifyPropertyChangedFor(nameof(IsMonthStep)), NotifyPropertyChangedFor(nameof(IsYearStep))]
    private ChartStep _step = ChartStep.Month;

    public bool IsWeek => Step == ChartStep.Week;
    public bool IsMonthStep => Step == ChartStep.Month;
    public bool IsYearStep => Step == ChartStep.Year;

    partial void OnStepChanged(ChartStep value) => Rebuild();

    [RelayCommand]
    private void SetStep(string? key)
    {
        if (Enum.TryParse<ChartStep>(key, out var step)) Step = step;
    }

    // ----- the categories -----

    public ObservableCollection<CategoryChip> Chips { get; } = [];

    [ObservableProperty] private IReadOnlyList<CategoryOption> _choices = [];

    /// <summary>The box that adds a category: whatever is chosen becomes a tag and the box is empty again.</summary>
    [ObservableProperty] private Guid? _pickedCategory;

    public bool HasChips => Chips.Count > 0;

    /// <summary>The hint above an empty chart: there is nothing to draw yet.</summary>
    public bool ShowHint => HasData && Chips.Count == 0;

    partial void OnPickedCategoryChanged(Guid? value)
    {
        if (value is not { } id) return;
        AddCategory(id);
        PickedCategory = null;
    }

    /// <summary>Adds a line for a category (with its subcategories); false when it is already there, unknown or the chart is full.</summary>
    public bool AddCategory(Guid id)
    {
        if (!_byId.TryGetValue(id, out var category) || Chips.Any(c => c.Id == id)) return false;
        if (Chips.Count >= MaxChips)
        {
            Status = Res.Format("ChartTooMany", MaxChips);
            return false;
        }
        Chips.Add(MakeChip(category));
        Status = "";
        AssignColors();
        Rebuild();
        return true;
    }

    [RelayCommand]
    private void RemoveChip(CategoryChip? chip)
    {
        if (chip is null || !Chips.Remove(chip)) return;
        AssignColors();
        Rebuild();
    }

    /// <summary>Replaces a category by its subcategories (each with its own line).</summary>
    [RelayCommand]
    private void SplitChip(CategoryChip? chip)
    {
        if (chip is null || !Chips.Contains(chip)) return;
        var children = _categories.Where(c => c.ParentId == chip.Id && !c.IsArchived).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();
        if (children.Count == 0) return;
        var at = Chips.IndexOf(chip);
        Chips.RemoveAt(at);
        foreach (var child in children)
        {
            if (Chips.Any(c => c.Id == child.Id) || Chips.Count >= MaxChips) continue;
            Chips.Insert(Math.Min(at++, Chips.Count), MakeChip(child));
        }
        AssignColors();
        Rebuild();
    }

    [RelayCommand]
    private void ClearChips()
    {
        Chips.Clear();
        Rebuild();
    }

    /// <summary>Replaces the choice with the biggest main categories of the range: expenses or incomes.</summary>
    public void AddTop(int count, TransactionType type)
    {
        if (From is not { } from || To is not { } to) return;
        var totals = new Dictionary<Guid, decimal>();
        foreach (var t in _all)
        {
            if (t.Type != type || t.Date < from || t.Date > to || t.CategoryId is not { } id) continue;
            var root = RootOf(id);
            totals[root] = totals.GetValueOrDefault(root) + t.AmountPln;
        }

        Chips.Clear();
        foreach (var (id, _) in totals.Where(kv => kv.Value > 0 && _byId.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(Math.Min(count, MaxChips)))
            Chips.Add(MakeChip(_byId[id]));
        AssignColors();
        Rebuild();
    }

    private Guid RootOf(Guid id)
    {
        for (var guard = 0; guard < 16 && _byId.TryGetValue(id, out var c) && c.ParentId is { } parent; guard++) id = parent;
        return id;
    }

    private CategoryChip MakeChip(Category category)
    {
        var ids = CategoryHelpers.WithDescendants(category.Id, _categories);
        var chip = new CategoryChip(category, CategoryHelpers.PathOf(category.Id, _byId), _categories.Any(c => c.ParentId == category.Id && !c.IsArchived), ids);
        return chip;
    }

    /// <summary>Every line gets a colour of its own: the category's colour when it has one nobody else uses, else the next free one of the palette.</summary>
    private void AssignColors()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var palette = ChartFactory.Palette;
        foreach (var chip in Chips)
        {
            var own = _byId.TryGetValue(chip.Id, out var c) ? c.Color : "";
            string pick;
            if (own.Length > 0 && !used.Contains(own)) pick = own;
            else pick = palette.FirstOrDefault(p => !used.Contains(p)) ?? palette[Chips.IndexOf(chip) % palette.Length];
            used.Add(pick);
            chip.Color = pick;
        }
    }

    // ----- the chart -----

    [ObservableProperty] private IEnumerable<ISeries> _series = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _xAxes = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _yAxes = [];
    [ObservableProperty] private string _pointsText = "";
    [ObservableProperty] private string _status = "";

    public LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint LegendTextPaint => ChartFactory.TextPaint();

    /// <summary>Reads the data again (it changed, or the tab is shown the first time); what was chosen stays.</summary>
    public void Reload()
    {
        Stale = false;
        _all = _core.Reader.GetTransactions().ToList();
        _categories = _core.Reader.GetCategories();
        _byId = _categories.ToDictionary(c => c.Id);
        Choices = CategoryHelpers.Flatten(_categories, c => !c.IsArchived, alphabetical: true);

        // a line of a category that has disappeared is dropped
        foreach (var gone in Chips.Where(c => !_byId.ContainsKey(c.Id)).ToList()) Chips.Remove(gone);

        var firstDay = _all.Count == 0 ? (DateOnly?)null : _all.Min(t => t.Date);
        var lastDay = _all.Count == 0 ? (DateOnly?)null : _all.Max(t => t.Date);
        var previousFrom = From;
        var previousTo = To;
        var followStart = previousFrom is null || previousFrom == _oldMin;
        var followEnd = previousTo is null || previousTo == _oldMax;
        _oldMin = firstDay;
        _oldMax = lastDay;

        HasData = firstDay is not null;
        MinDate = firstDay;
        MaxDate = lastDay;
        MinText = firstDay?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        MaxText = lastDay?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        if (firstDay is not { } min || lastDay is not { } max)
        {
            RangeMin = 0;
            RangeMax = 1;
            Series = [];
            XAxes = [];
            return;
        }

        RangeMin = min.DayNumber;
        RangeMax = Math.Max(max.DayNumber, min.DayNumber + 1);
        // the whole range at first; later the ends stay where the user put them, except an end that sat at the end of the data goes on following it
        var from = followStart || previousFrom is null ? min : previousFrom.Value;
        var to = followEnd || previousTo is null ? max : previousTo.Value;
        var immediate = RebuildDelay;
        RebuildDelay = TimeSpan.Zero;
        try { SetRange(from, to, movedFrom: true); }
        finally { RebuildDelay = immediate; }

        if (!_initialized)
        {
            _initialized = true;
            if (Chips.Count == 0) AddTop(5, TransactionType.Expense);                      // something to look at straight away
        }
        else
        {
            AssignColors();
            Rebuild();
        }
    }

    private void ScheduleRebuild()
    {
        if (RebuildDelay <= TimeSpan.Zero)
        {
            Rebuild();
            return;
        }
        _timer ??= CreateTimer();
        _timer.Interval = RebuildDelay;
        _timer.Stop();
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Rebuild();
        };
        return timer;
    }

    /// <summary>Draws the lines: for every chosen category the sum of its entries (subcategories included) in each week, month or year of the range.</summary>
    public void Rebuild()
    {
        _timer?.Stop();
        if (!HasData || From is not { } from || To is not { } to)
        {
            Series = [];
            XAxes = [];
            PointsText = "";
            return;
        }

        var starts = ChartBuckets.Starts(from, to, Step);
        var index = new Dictionary<DateOnly, int>();
        for (var i = 0; i < starts.Count; i++) index[starts[i]] = i;

        var values = Chips.Select(_ => new decimal[starts.Count]).ToList();
        foreach (var t in _all)
        {
            if (t.Date < from || t.Date > to || t.CategoryId is not { } id) continue;
            var bucket = index[ChartBuckets.Start(t.Date, Step)];
            for (var c = 0; c < Chips.Count; c++)
                if (Chips[c].Counts(t) && Chips[c].Ids.Contains(id)) values[c][bucket] += t.AmountPln;
        }

        for (var c = 0; c < Chips.Count; c++)
            Chips[c].TotalText = values[c].Sum().ToString("C0", CultureInfo.CurrentCulture);

        Series = ChartFactory.CategoryLines(Chips.Select((chip, c) => (chip.Name, chip.Color, (IReadOnlyList<decimal>)values[c])).ToList());
        XAxes = ChartFactory.BucketAxes(starts.Select(s => ChartBuckets.Label(s, Step)).ToList());
        YAxes = ChartFactory.MoneyAxes();
        PointsText = Res.Format("ChartPointsText", starts.Count, Res.Get(Step switch { ChartStep.Week => "ChartStepWeeks", ChartStep.Month => "ChartStepMonths", _ => "ChartStepYears" }));
    }
}
