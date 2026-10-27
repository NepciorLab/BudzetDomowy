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

    /// <summary>The most labels written on the horizontal axis.</summary>
    public const int MaxLabels = 20;

    /// <summary>
    /// The labels of the horizontal axis (empty where nothing is written). They stand at the same moments of the year: every month,
    /// every quarter (January, April, July, October), every half year (January, July), every year (January) and so on, with the
    /// finest of these that keeps the number of labels within <paramref name="maxLabels"/>. A week gets the label of the month it starts.
    /// </summary>
    public static IReadOnlyList<string> AxisLabels(IReadOnlyList<DateOnly> starts, ChartStep step, int maxLabels = MaxLabels)
    {
        var labels = new string[starts.Count];
        Array.Fill(labels, "");
        if (starts.Count == 0) return labels;

        // the number a point has on a scale of whole months or years, or -1 when the point does not hold the first day of a month
        var marks = new int[starts.Count];
        for (var i = 0; i < starts.Count; i++)
            marks[i] = step switch
            {
                ChartStep.Year => starts[i].Year,
                ChartStep.Month => starts[i].Year * 12 + starts[i].Month - 1,
                _ => FirstOfMonthIn(starts[i]) is { } first ? first.Year * 12 + first.Month - 1 : -1,
            };

        var intervals = step == ChartStep.Year ? new[] { 1, 2, 5, 10, 20, 50, 100 } : new[] { 1, 3, 6, 12, 24, 60, 120 };
        var interval = intervals[^1];
        foreach (var candidate in intervals)
        {
            if (marks.Count(m => m >= 0 && m % candidate == 0) > maxLabels) continue;
            interval = candidate;
            break;
        }

        var any = false;
        for (var i = 0; i < starts.Count; i++)
        {
            if (marks[i] < 0 || marks[i] % interval != 0) continue;
            any = true;
            labels[i] = step == ChartStep.Year ? starts[i].ToString("yyyy", CultureInfo.CurrentCulture)
                : step == ChartStep.Month ? starts[i].ToString("MMM yy", CultureInfo.CurrentCulture)
                : FirstOfMonthIn(starts[i])!.Value.ToString("MMM yy", CultureInfo.CurrentCulture);
        }

        if (!any)                                                              // a few weeks that hold no first of a month: the weeks themselves
        {
            var stride = Math.Max(1, (int)Math.Ceiling(starts.Count / (double)maxLabels));
            for (var i = 0; i < starts.Count; i += stride) labels[i] = Label(starts[i], step);
        }
        return labels;
    }

    /// <summary>The first day of a month that falls in the week beginning on <paramref name="weekStart"/>, if any.</summary>
    private static DateOnly? FirstOfMonthIn(DateOnly weekStart)
    {
        var first = weekStart.Day == 1 ? weekStart : new DateOnly(weekStart.Year, weekStart.Month, 1).AddMonths(1);
        return first <= weekStart.AddDays(6) ? first : null;
    }

    /// <summary>
    /// The period a point stands for, for the tooltip: the month ("wrzesień 2017") or the year, or the dates when a week is meant or the
    /// point is cut by an end of the chosen range (the first and the last month are often not whole).
    /// </summary>
    public static string Period(DateOnly start, ChartStep step, DateOnly from, DateOnly to)
    {
        var end = Next(start, step).AddDays(-1);
        var a = start < from ? from : start;
        var b = end > to ? to : end;
        if (a == start && b == end && step != ChartStep.Week)
            return step == ChartStep.Year ? start.ToString("yyyy", CultureInfo.CurrentCulture) : start.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return a == b
            ? a.ToString("d", CultureInfo.CurrentCulture)
            : a.ToString("d", CultureInfo.CurrentCulture) + " – " + b.ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>How the points of a line are smoothed: the average or the median of the neighbours.</summary>
public enum SmoothMethod { Average, Median }

/// <summary>Smoothing of a line: every point becomes the average (or the median) of a window of points around it.</summary>
public static class ChartSmoothing
{
    /// <summary>The windows of the slider: none, then this many points (months, weeks or years, as the step is).</summary>
    public static readonly int[] Windows = [0, 3, 6, 12];

    /// <summary>
    /// The window is centred on the point (an even one has one point more behind than ahead); at the ends it is cut to the points there are,
    /// so the line keeps its first and last point and does not lag behind the data.
    /// </summary>
    public static decimal[] Apply(decimal[] values, int window, SmoothMethod method)
    {
        if (window <= 1 || values.Length < 2) return values;
        var result = new decimal[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var first = i - (window - 1) / 2;
            var from = Math.Max(0, first);
            var to = Math.Min(values.Length - 1, first + window - 1);
            var slice = values[from..(to + 1)];
            if (method == SmoothMethod.Average)
            {
                result[i] = slice.Sum() / slice.Length;
                continue;
            }

            Array.Sort(slice);
            result[i] = slice.Length % 2 == 1 ? slice[slice.Length / 2] : (slice[slice.Length / 2 - 1] + slice[slice.Length / 2]) / 2;
        }
        return result;
    }
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

    private CategoryChip(Guid id, string name, TransactionType total)
    {
        Id = id;
        Name = name;
        CategoryType = CategoryType.Both;
        Ids = [];
        TotalOf = total;
    }

    /// <summary>Stands for no category: the line of everything spent (or earned), also what has no category.</summary>
    public static readonly Guid AllExpensesId = new("00000000-0000-0000-0000-00000000e001");

    public static readonly Guid AllIncomeId = new("00000000-0000-0000-0000-00000000e002");

    public static CategoryChip Total(TransactionType type, string name) =>
        new(type == TransactionType.Income ? AllIncomeId : AllExpensesId, name, type);

    /// <summary>The kind of entries a line of the whole income or of all expenses sums up; null for a category.</summary>
    public TransactionType? TotalOf { get; }

    public bool IsTotal => TotalOf is not null;

    internal bool Matches(Transaction t) =>
        TotalOf is { } type ? t.Type == type : t.CategoryId is { } id && Counts(t) && Ids.Contains(id);

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

    // ----- the smoothing -----

    /// <summary>The position of the slider: 0 is no smoothing, then 1, 2 and 3 stand for the windows of 3, 6 and 12 points.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SmoothWindow)), NotifyPropertyChangedFor(nameof(SmoothText)), NotifyPropertyChangedFor(nameof(IsSmoothed))]
    private double _smoothIndex;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAverage)), NotifyPropertyChangedFor(nameof(IsMedian))]
    private SmoothMethod _smoothMethod = SmoothMethod.Average;

    public int SmoothWindow => ChartSmoothing.Windows[Math.Clamp((int)Math.Round(SmoothIndex), 0, ChartSmoothing.Windows.Length - 1)];

    public bool IsSmoothed => SmoothWindow > 1;

    public string SmoothText => IsSmoothed ? SmoothWindow.ToString(CultureInfo.CurrentCulture) : Res.Get("ChartSmoothNone");

    public bool IsAverage => SmoothMethod == SmoothMethod.Average;

    public bool IsMedian => SmoothMethod == SmoothMethod.Median;

    partial void OnSmoothIndexChanged(double value) => ScheduleRebuild();

    partial void OnSmoothMethodChanged(SmoothMethod value)
    {
        if (IsSmoothed) Rebuild();
    }

    [RelayCommand]
    private void SetSmoothMethod(string? key)
    {
        if (Enum.TryParse<SmoothMethod>(key, out var method)) SmoothMethod = method;
    }

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

    /// <summary>Adds a single line for everything spent (or earned) in the range, whatever the category.</summary>
    public bool AddTotal(TransactionType type)
    {
        var id = type == TransactionType.Income ? CategoryChip.AllIncomeId : CategoryChip.AllExpensesId;
        if (Chips.Any(c => c.Id == id)) return false;
        if (Chips.Count >= MaxChips)
        {
            Status = Res.Format("ChartTooMany", MaxChips);
            return false;
        }
        Chips.Add(CategoryChip.Total(type, Res.Get(type == TransactionType.Income ? "ChartAllIncome" : "ChartAllExpenses")));
        Status = "";
        AssignColors();
        Rebuild();
        return true;
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
            var own = chip.TotalOf is { } total ? (total == TransactionType.Income ? "#43A047" : "#E53935")
                : _byId.TryGetValue(chip.Id, out var c) ? c.Color : "";
            string pick;
            if (own.Length > 0 && !used.Contains(own)) pick = own;
            else pick = palette.FirstOrDefault(p => !used.Contains(p)) ?? palette[Chips.IndexOf(chip) % palette.Length];
            used.Add(pick);
            chip.Color = pick;
        }
    }

    // ----- the chart -----

    [ObservableProperty] private IEnumerable<ISeries> _series = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _xAxes = ChartFactory.LabelAxes([""]);        // never empty: LiveCharts cannot measure a chart without axes
    [ObservableProperty] private IEnumerable<ICartesianAxis> _yAxes = ChartFactory.MoneyAxes();
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
        foreach (var gone in Chips.Where(c => !c.IsTotal && !_byId.ContainsKey(c.Id)).ToList()) Chips.Remove(gone);

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
            XAxes = ChartFactory.LabelAxes([""]);
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
            if (Chips.Count == 0)                                                          // something to look at straight away: the sum of the expenses and of the income
            {
                AddTotal(TransactionType.Expense);
                AddTotal(TransactionType.Income);
            }
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
            XAxes = ChartFactory.LabelAxes([""]);
            PointsText = "";
            return;
        }

        var starts = ChartBuckets.Starts(from, to, Step);
        var index = new Dictionary<DateOnly, int>();
        for (var i = 0; i < starts.Count; i++) index[starts[i]] = i;

        var values = Chips.Select(_ => new decimal[starts.Count]).ToList();
        foreach (var t in _all)
        {
            if (t.Date < from || t.Date > to) continue;
            var bucket = index[ChartBuckets.Start(t.Date, Step)];
            for (var c = 0; c < Chips.Count; c++)
                if (Chips[c].Matches(t)) values[c][bucket] += t.AmountPln;
        }

        for (var c = 0; c < Chips.Count; c++)
            Chips[c].TotalText = values[c].Sum().ToString("C0", CultureInfo.CurrentCulture);

        var step = Step;
        var window = SmoothWindow;
        var method = SmoothMethod;
        Series = ChartFactory.CategoryLines(Chips.Select((chip, c) => (chip.Name, chip.Color, (IReadOnlyList<decimal>)ChartSmoothing.Apply(values[c], window, method))).ToList(),
            i => i >= 0 && i < starts.Count ? ChartBuckets.Period(starts[i], step, from, to) : "");
        XAxes = ChartFactory.SparseAxes(ChartBuckets.AxisLabels(starts, step));
        YAxes = ChartFactory.MoneyAxes();
        PointsText = Res.Format("ChartPointsText", starts.Count, Res.Get(Step switch { ChartStep.Week => "ChartStepWeeks", ChartStep.Month => "ChartStepMonths", _ => "ChartStepYears" }));
    }
}
