using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record TypeFilterOption(TransactionType? Type, string Label)
{
    public override string ToString() => Label;
}

public enum ViewMode { Week, Month, Year, All, Range }

public sealed partial class TransactionsViewModel : ObservableObject, IPeriodNavigable
{
    public static readonly TypeChoice ExpenseChoice = new(false, Res.Get("TypeExpense"));
    public static readonly TypeChoice IncomeChoice = new(true, Res.Get("TypeIncome"));

    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly ExportActions _exports;
    private IReadOnlyDictionary<Guid, Category> _categoriesById = new Dictionary<Guid, Category>();
    private IReadOnlyDictionary<Guid, SpecialEvent> _eventsById = new Dictionary<Guid, SpecialEvent>();
    private IReadOnlyList<CategoryOption> _expenseChoices = [];
    private IReadOnlyList<CategoryOption> _incomeChoices = [];
    private IReadOnlyList<TransactionRowViewModel> _selected = [];
    private bool _suspendReload;
    private bool _reloadPending;

    public ObservableCollection<TransactionRowViewModel> Rows { get; } = [];
    public IReadOnlyList<TypeChoice> TypeChoices { get; } = [ExpenseChoice, IncomeChoice];
    public IReadOnlyList<TypeFilterOption> TypeFilters { get; } =
    [
        new(null, Res.Get("FilterAllTypes")),
        new(TransactionType.Expense, Res.Get("FilterExpenses")),
        new(TransactionType.Income, Res.Get("FilterIncome")),
    ];

    /// <summary>All categories (also archived) for the filter box.</summary>
    [ObservableProperty] private IReadOnlyList<CategoryOption> _filterCategories = [];
    [ObservableProperty] private IReadOnlyList<EventOption> _filterEvents = [];
    [ObservableProperty] private IReadOnlyList<EventOption> _eventChoices = [];

    // what period the table shows: a week, month or year around the anchor date, or a free range
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWeekMode)), NotifyPropertyChangedFor(nameof(IsMonthMode)),
        NotifyPropertyChangedFor(nameof(IsYearMode)), NotifyPropertyChangedFor(nameof(IsRangeMode)), NotifyPropertyChangedFor(nameof(IsAllMode)),
        NotifyPropertyChangedFor(nameof(HasArrows))] private ViewMode _mode = ViewMode.Month;
    [ObservableProperty] private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _periodTitle = "";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private Guid? _categoryFilter;
    [ObservableProperty] private Guid _eventFilter = Guid.Empty;
    [ObservableProperty] private TypeFilterOption? _typeFilter;
    [ObservableProperty] private string _search = "";
    // totals of everything the filters let through, and separately of the selected rows
    [ObservableProperty] private string _allLabel = "";
    [ObservableProperty] private string _allExpenseText = "";
    [ObservableProperty] private string _allIncomeText = "";
    [ObservableProperty] private string _allBalanceText = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _selectionLabel = "";
    [ObservableProperty] private string _selectionExpenseText = "";
    [ObservableProperty] private string _selectionIncomeText = "";
    [ObservableProperty] private string _selectionBalanceText = "";
    [ObservableProperty] private bool _isEmpty;

    public int ChoicesVersion { get; private set; }

    public bool IsWeekMode => Mode == ViewMode.Week;
    public bool IsMonthMode => Mode == ViewMode.Month;
    public bool IsYearMode => Mode == ViewMode.Year;
    public bool IsRangeMode => Mode == ViewMode.Range;
    public bool IsAllMode => Mode == ViewMode.All;

    /// <summary>Week, month and year have a previous and a next period; "all" and a free range do not.</summary>
    public bool HasArrows => Mode is ViewMode.Week or ViewMode.Month or ViewMode.Year;

    public TransactionsViewModel(BudgetCore core, DataHub hub, ToastService toast, ExportActions exports)
    {
        _core = core;
        _toast = toast;
        _exports = exports;
        _typeFilter = TypeFilters[0];
        UiPreferences.Instance.OnAppearanceChanged(this, self =>
        {
            foreach (var row in self.Rows) row.Refresh(row.Model, force: true);
            self.UpdateSummary();
        });
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnModeChanged(ViewMode value) => Reload();
    partial void OnAnchorChanged(DateOnly value) => Reload();
    partial void OnFromChanged(DateTime? value) => Reload();
    partial void OnToChanged(DateTime? value) => Reload();
    partial void OnCategoryFilterChanged(Guid? value) => Reload();
    partial void OnEventFilterChanged(Guid value) => Reload();
    partial void OnTypeFilterChanged(TypeFilterOption? value) => Reload();
    partial void OnSearchChanged(string value) => Reload();

    /// <summary>The grid is in the middle of a cell edit: do not rebuild rows under it.</summary>
    public void SetEditing(bool editing)
    {
        _suspendReload = editing;
        if (!editing && _reloadPending) Reload();
    }

    public void SetSelection(IEnumerable<TransactionRowViewModel> selected)
    {
        _selected = selected.ToList();
        UpdateSummary();
    }

    public void Reload()
    {
        if (_suspendReload)
        {
            _reloadPending = true;
            return;
        }
        _reloadPending = false;

        var categories = _core.Reader.GetCategories();
        _categoriesById = categories.ToDictionary(c => c.Id);
        var specials = _core.Reader.GetSpecialEvents();
        _eventsById = specials.ToDictionary(e => e.Id);

        _expenseChoices = CategoryHelpers.ForEntry(categories, TransactionType.Expense);
        _incomeChoices = CategoryHelpers.ForEntry(categories, TransactionType.Income);
        ChoicesVersion++;

        var allOption = new EventOption(Guid.Empty, Res.Get("FilterAllEvents"));
        var noneOption = new EventOption(Guid.Empty, Res.Get("NoEvent"));
        var eventOptions = specials.OrderByDescending(e => e.DateFrom).Select(e => new EventOption(e.Id, e.Name)).ToList();
        FilterEvents = [allOption, .. eventOptions];
        EventChoices = [noneOption, .. eventOptions];
        FilterCategories = CategoryHelpers.Flatten(categories, alphabetical: true)
            .Select(o => o.Category.IsArchived ? o with { DisplayName = o.DisplayName + " " + Res.Get("ArchivedSuffix") } : o)
            .ToList();

        var (from, to) = CurrentRange();
        PeriodTitle = TitleOf();
        IEnumerable<Transaction> query = _core.Reader.GetTransactions(from, to);

        if (CategoryFilter is { } cat)
        {
            var ids = CategoryHelpers.WithDescendants(cat, categories);
            query = query.Where(x => x.CategoryId is { } c && ids.Contains(c));
        }
        if (EventFilter != Guid.Empty) query = query.Where(x => x.EventId == EventFilter);
        if (TypeFilter?.Type is { } type) query = query.Where(x => x.Type == type);

        var needle = Search.Trim();
        if (needle.Length > 0)
        {
            query = query.Where(x =>
                x.Note.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || x.Place.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || CategoryPath(x.CategoryId).Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || x.Amount.ToString("N2", CultureInfo.CurrentCulture).Contains(needle, StringComparison.CurrentCultureIgnoreCase));
        }

        var target = query.OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        Reconcile(target);
        IsEmpty = Rows.Count == 0;
        UpdateSummary();
    }

    /// <summary>Brings the row collection in line with the target list with minimal changes (keeps scroll position and selection).</summary>
    private void Reconcile(List<Transaction> target)
    {
        var byId = Rows.ToDictionary(r => r.Id);
        for (var i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i < Rows.Count && Rows[i].Id == t.Id)
            {
                Rows[i].Refresh(t, force: false);
            }
            else if (byId.TryGetValue(t.Id, out var existing))
            {
                Rows.Move(Rows.IndexOf(existing), i);
                existing.Refresh(t);
            }
            else
            {
                var row = new TransactionRowViewModel(this, t);
                Rows.Insert(i, row);
                byId[t.Id] = row;
            }
        }
        while (Rows.Count > target.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    /// <summary>The dates the table covers (either end may be open in the free-range mode).</summary>
    private (DateOnly? From, DateOnly? To) CurrentRange()
    {
        if (Mode == ViewMode.All) return (null, null);
        if (Mode == ViewMode.Range)
            return (From is { } f ? DateOnly.FromDateTime(f) : null, To is { } t ? DateOnly.FromDateTime(t) : null);
        var kind = Mode switch { ViewMode.Week => PeriodKind.Week, ViewMode.Year => PeriodKind.Year, _ => PeriodKind.Month };
        var (a, b) = PeriodRanges.Range(kind, Anchor);
        return (a, b);
    }

    private string TitleOf()
    {
        switch (Mode)
        {
            case ViewMode.Week:
                var (a, b) = PeriodRanges.WeekOf(Anchor);
                return Res.Format("PeriodWeekTitle", a.ToString("d.MM", CultureInfo.CurrentCulture), b.ToString("d.MM.yyyy", CultureInfo.CurrentCulture), PeriodRanges.IsoWeek(Anchor));
            case ViewMode.Year:
                return Anchor.Year.ToString(CultureInfo.CurrentCulture);
            case ViewMode.All:
                return Res.Get("PeriodAllTitle");
            case ViewMode.Range:
                return Res.Format("PeriodRangeTitle",
                    From is { } f ? f.ToString("d", CultureInfo.CurrentCulture) : "…",
                    To is { } t ? t.ToString("d", CultureInfo.CurrentCulture) : "…");
            default:
                var m = Anchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
                return char.ToUpper(m[0], CultureInfo.CurrentCulture) + m[1..];
        }
    }

    public void PreviousPeriod() => Step(-1);
    public void NextPeriod() => Step(+1);

    private void Step(int direction)
    {
        if (!HasArrows) return;
        var kind = Mode switch { ViewMode.Week => PeriodKind.Week, ViewMode.Year => PeriodKind.Year, _ => PeriodKind.Month };
        Anchor = PeriodRanges.Shift(kind, Anchor, direction);
    }

    [RelayCommand] private void Previous() => PreviousPeriod();
    [RelayCommand] private void Next() => NextPeriod();
    [RelayCommand] private void Today() => Anchor = DateOnly.FromDateTime(DateTime.Today);

    [RelayCommand]
    private void SetMode(string? key)
    {
        if (Enum.TryParse<ViewMode>(key, out var mode)) Mode = mode;
    }

    /// <summary>Esc in the table: deselect everything.</summary>
    public event Action? SelectionClearRequested;
    public void ClearSelection() => SelectionClearRequested?.Invoke();

    private void UpdateSummary()
    {
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        void Fill(IReadOnlyList<TransactionRowViewModel> rows, string labelKey, out string label, out string expense, out string income, out string balance)
        {
            var e = rows.Where(r => !r.IsIncome).Sum(r => r.Model.AmountPln);
            var i = rows.Where(r => r.IsIncome).Sum(r => r.Model.AmountPln);
            label = Res.Format(labelKey, rows.Count);
            expense = Res.Format("SummaryExpenses", Money(e));
            income = Res.Format("SummaryIncome", Money(i));
            balance = Res.Format("SummaryBalance", Money(i - e));
        }

        Fill(Rows.ToList(), "SummaryAll", out var l, out var ex, out var inc, out var bal);
        AllLabel = l; AllExpenseText = ex; AllIncomeText = inc; AllBalanceText = bal;

        HasSelection = _selected.Count > 0;
        if (HasSelection)
        {
            Fill(_selected.ToList(), "SummarySelectedLabel", out l, out ex, out inc, out bal);
            SelectionLabel = l; SelectionExpenseText = ex; SelectionIncomeText = inc; SelectionBalanceText = bal;
        }
    }

    public string CategoryPath(Guid? id) => CategoryHelpers.PathOf(id, _categoriesById);

    public string CategoryColor(Guid? id) => id is { } c && _categoriesById.TryGetValue(c, out var cat) ? cat.Color : "";

    public string CategoryIcon(Guid? id) => id is { } c && _categoriesById.TryGetValue(c, out var cat) ? cat.Icon : "";

    public string EventName(Guid? id) => id is { } e && _eventsById.TryGetValue(e, out var s) ? s.Name : "";

    public IReadOnlyList<CategoryOption> ChoicesFor(TransactionType type, Guid? current)
    {
        var list = type == TransactionType.Income ? _incomeChoices : _expenseChoices;
        if (current is not { } id || list.Any(o => o.Id == id) || !_categoriesById.TryGetValue(id, out var c)) return list;
        var name = CategoryHelpers.PathOf(id, _categoriesById) + (c.IsArchived ? " " + Res.Get("ArchivedSuffix") : "");
        return [.. list, new CategoryOption(c, name, 0)];
    }

    /// <summary>Saves an edit made in a cell.</summary>
    internal void Edit(TransactionRowViewModel row, Func<Transaction, Transaction> change)
    {
        try
        {
            row.Refresh(_core.Transactions.Update(change(row.Model)), force: true);
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
            row.Refresh(row.Model, force: true);
        }
    }

    internal void LearnRule(string note, Guid categoryId)
    {
        try { _core.Rules.Learn(note, categoryId, null); }
        catch (UpdateRequiredException) { }
    }

    // ----- commands -----

    [RelayCommand]
    public void DeleteSelected()
    {
        var items = _selected.Select(r => r.Model).ToList();
        if (items.Count == 0) return;
        try
        {
            _core.Transactions.Delete(items);
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
            return;
        }
        _toast.Show(Res.Format("DeletedCount", items.Count), () => _core.Transactions.Restore(items));
    }

    public void ChangeCategoryOfSelected(Guid? categoryId)
    {
        var ids = _selected.Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        var notes = _selected.Select(r => r.Model.Place).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        IReadOnlyList<Transaction> before;
        try
        {
            before = _core.Transactions.ChangeCategory(ids, categoryId);
            if (categoryId is { } c) foreach (var note in notes) LearnRule(note, c);
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
            return;
        }
        _toast.Show(Res.Format("CategoryChangedCount", ids.Count), () => _core.Transactions.Restore(before));
    }

    /// <summary>Exports what the table shows right now (the period and the filters apply).</summary>
    [RelayCommand]
    private void ExportXlsx() => _exports.Transactions(Rows.Select(r => r.Model).ToList(), csv: false);

    [RelayCommand]
    private void ClearFilters()
    {
        CategoryFilter = null;
        EventFilter = Guid.Empty;
        TypeFilter = TypeFilters[0];
        Search = "";
    }

    /// <summary>Whether any of the filters under the period is set (Esc then clears them).</summary>
    public bool HasActiveFilters => CategoryFilter is not null || EventFilter != Guid.Empty || TypeFilter?.Type is not null || Search.Length > 0;

    public bool ClearFiltersIfAny()
    {
        if (!HasActiveFilters) return false;
        ClearFilters();
        return true;
    }


}
