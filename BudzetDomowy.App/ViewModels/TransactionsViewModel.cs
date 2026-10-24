using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record TypeFilterOption(TransactionType? Type, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class TransactionsViewModel : ObservableObject
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

    [ObservableProperty] private DateTime? _from = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private Guid? _categoryFilter;
    [ObservableProperty] private Guid _eventFilter = Guid.Empty;
    [ObservableProperty] private TypeFilterOption? _typeFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summaryLabel = "";
    [ObservableProperty] private string _expenseText = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private bool _isEmpty;

    public int ChoicesVersion { get; private set; }

    public TransactionsViewModel(BudgetCore core, DataHub hub, ToastService toast, ExportActions exports)
    {
        _core = core;
        _toast = toast;
        _exports = exports;
        _typeFilter = TypeFilters[0];
        hub.Changed += (_, _) => Reload();
        Reload();
    }

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
        FilterCategories = CategoryHelpers.Flatten(categories)
            .Select(o => o.Category.IsArchived ? o with { DisplayName = o.DisplayName + " " + Res.Get("ArchivedSuffix") } : o)
            .ToList();

        var from = From is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = To is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
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

    private void UpdateSummary()
    {
        var rows = _selected.Count > 0 ? _selected : Rows.ToList();
        var expense = rows.Where(r => !r.IsIncome).Sum(r => r.Model.AmountPln);
        var income = rows.Where(r => r.IsIncome).Sum(r => r.Model.AmountPln);
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);
        SummaryLabel = Res.Format(_selected.Count > 0 ? "SummarySelected" : "SummaryVisible", rows.Count);
        ExpenseText = Res.Format("SummaryExpenses", Money(expense));
        IncomeText = Res.Format("SummaryIncome", Money(income));
        BalanceText = Res.Format("SummaryBalance", Money(income - expense));
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

    /// <summary>Exports what the table shows right now (the current filters apply).</summary>
    [RelayCommand]
    private void ExportXlsx() => _exports.Transactions(Rows.Select(r => r.Model).ToList(), csv: false);

    [RelayCommand]
    private void ExportCsv() => _exports.Transactions(Rows.Select(r => r.Model).ToList(), csv: true);

    [RelayCommand]
    private void ClearFilters()
    {
        From = null;
        To = null;
        CategoryFilter = null;
        EventFilter = Guid.Empty;
        TypeFilter = TypeFilters[0];
        Search = "";
    }

    [RelayCommand]
    private void ThisMonth()
    {
        var today = DateTime.Today;
        From = new DateTime(today.Year, today.Month, 1);
        To = null;
    }
}
