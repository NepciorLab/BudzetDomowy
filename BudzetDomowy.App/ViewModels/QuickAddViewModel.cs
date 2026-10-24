using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

/// <summary>Entry of the event picker; <see cref="Key"/> is Guid.Empty for "no event".</summary>
public sealed record EventOption(Guid Key, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class QuickAddViewModel : ObservableObject
{
    private enum EventSource { None, Default, Rule, Manual }

    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private IReadOnlyList<CategoryRule> _rules = [];
    private IReadOnlyList<SpecialEvent> _specialEvents = [];
    private bool _applying;
    private bool _categoryManual;
    private EventSource _eventSource = EventSource.None;

    [ObservableProperty] private DateTime? _date = DateTime.Today;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private Guid? _categoryId;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private Guid _eventKey = Guid.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypeLabel))] private bool _isIncome;
    [ObservableProperty] private IReadOnlyList<CategoryOption> _categories = [];
    [ObservableProperty] private IReadOnlyList<EventOption> _events = [];
    [ObservableProperty] private string _errorMessage = "";

    public string TypeLabel => Res.Get(IsIncome ? "TypeIncome" : "TypeExpense");

    /// <summary>Raised after a successful save: the view clears its combo boxes and focuses the amount.</summary>
    public event Action? Saved;

    /// <summary>Raised when the amount is missing or invalid: the view focuses the amount.</summary>
    public event Action? AmountRejected;

    public QuickAddViewModel(BudgetCore core, DataHub hub, ToastService toast)
    {
        _core = core;
        _toast = toast;
        hub.Changed += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        _rules = _core.Reader.GetCategoryRules();
        _specialEvents = _core.Reader.GetSpecialEvents();
        var all = _core.Reader.GetCategories();
        Categories = CategoryHelpers.ForEntry(all, IsIncome ? TransactionType.Income : TransactionType.Expense);

        var options = new List<EventOption> { new(Guid.Empty, Res.Get("NoEvent")) };
        options.AddRange(_specialEvents.OrderByDescending(e => e.DateFrom).Select(e => new EventOption(e.Id, e.Name)));
        Events = options;
        ApplyDefaultEvent();
    }

    partial void OnNoteChanged(string value)
    {
        if (_applying) return;
        var s = CategoryRuleService.Suggest(value, _rules);
        if (s is null) return;

        _applying = true;
        if (!_categoryManual && Categories.Any(c => c.Id == s.CategoryId)) CategoryId = s.CategoryId;
        if (_eventSource is EventSource.None or EventSource.Default or EventSource.Rule
            && s.EventId is { } ev && Events.Any(e => e.Key == ev))
        {
            EventKey = ev;
            _eventSource = EventSource.Rule;
        }
        _applying = false;
    }

    partial void OnCategoryIdChanged(Guid? value)
    {
        if (!_applying) _categoryManual = value is not null;
    }

    partial void OnEventKeyChanged(Guid value)
    {
        if (!_applying) _eventSource = EventSource.Manual;
    }

    partial void OnDateChanged(DateTime? value) => ApplyDefaultEvent();

    partial void OnIsIncomeChanged(bool value)
    {
        Categories = CategoryHelpers.ForEntry(_core.Reader.GetCategories(), value ? TransactionType.Income : TransactionType.Expense);
        if (CategoryId is { } id && Categories.All(c => c.Id != id))
        {
            _applying = true;
            CategoryId = null;
            _applying = false;
        }
    }

    /// <summary>While today is inside an event's dates that event is suggested; choosing "no event" switches it off.</summary>
    private void ApplyDefaultEvent()
    {
        if (_eventSource is EventSource.Manual or EventSource.Rule) return;
        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        var active = SpecialEvents.ActiveOn(_specialEvents, date);
        _applying = true;
        EventKey = active?.Id ?? Guid.Empty;
        _eventSource = active is null ? EventSource.None : EventSource.Default;
        _applying = false;
    }

    [RelayCommand]
    private void ToggleType() => IsIncome = !IsIncome;

    [RelayCommand]
    public void Add()
    {
        ErrorMessage = "";
        if (!AmountParser.TryParse(AmountText, out var amount) || amount <= 0)
        {
            ErrorMessage = Res.Get("QuickAddBadAmount");
            AmountRejected?.Invoke();
            return;
        }

        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        var eventId = EventKey == Guid.Empty ? (Guid?)null : EventKey;
        var type = IsIncome ? TransactionType.Income : TransactionType.Expense;
        var categoryName = Categories.FirstOrDefault(c => c.Id == CategoryId)?.DisplayName;

        Transaction saved;
        try
        {
            saved = _core.Transactions.Add(date, type, amount, CategoryId, Note, eventId);
            if (CategoryId is { } cat && !string.IsNullOrWhiteSpace(Note))
                _core.Rules.Learn(Note, cat, _eventSource is EventSource.Manual or EventSource.Rule ? eventId : null);
        }
        catch (UpdateRequiredException)
        {
            ErrorMessage = Res.Get("UpdateRequiredShort");
            return;
        }

        var text = amount.ToString("C2", CultureInfo.CurrentCulture) + (categoryName is null ? "" : " · " + categoryName);
        _toast.Show(Res.Format("QuickAddSaved", text), () => _core.Transactions.Delete([saved]));
        ClearFields();
        Saved?.Invoke();
    }

    private void ClearFields()
    {
        _applying = true;
        AmountText = "";
        Note = "";
        CategoryId = null;
        IsIncome = false;
        _applying = false;
        _categoryManual = false;
        _eventSource = EventSource.None;
        ApplyDefaultEvent();
    }

    [RelayCommand]
    private void Clear()
    {
        ClearFields();
        ErrorMessage = "";
        Saved?.Invoke();
    }
}
