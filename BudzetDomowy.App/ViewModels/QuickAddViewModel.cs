using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Currency;
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

    private CancellationTokenSource? _rateCts;
    private bool _applyingRate;
    private bool _rateManual;
    private bool _rateBusy;
    private DateOnly? _rateDate;

    [ObservableProperty] private DateTime? _date = DateTime.Today;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForeign))] private string _currencyCode = CurrencyCatalog.Pln;
    [ObservableProperty] private IReadOnlyList<CurrencyOption> _currencies = [];
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _rateInfo = "";
    [ObservableProperty] private string _convertedText = "";
    [ObservableProperty] private Guid? _categoryId;
    [ObservableProperty] private string _place = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private Guid _eventKey = Guid.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypeLabel))] private bool _isIncome;
    [ObservableProperty] private IReadOnlyList<CategoryOption> _categories = [];
    [ObservableProperty] private IReadOnlyList<EventOption> _events = [];
    [ObservableProperty] private string _errorMessage = "";

    public string TypeLabel => Res.Get(IsIncome ? "TypeIncome" : "TypeExpense");
    public bool IsForeign => CurrencyCatalog.IsForeign(CurrencyCode);

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
        Currencies = CurrencyCatalog.Ordered(_core.Reader.GetRecentCurrencies());

        var options = new List<EventOption> { new(Guid.Empty, Res.Get("NoEvent")) };
        options.AddRange(_specialEvents.OrderByDescending(e => e.DateFrom).Select(e => new EventOption(e.Id, e.Name)));
        Events = options;
        ApplyDefaultEvent();
    }

    // ----- suggestions and defaults -----

    /// <summary>The place (shop, service) is what the learned rules are keyed on; the note is free text.</summary>
    partial void OnPlaceChanged(string value)
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

    partial void OnDateChanged(DateTime? value)
    {
        ApplyDefaultEvent();
        if (IsForeign) _ = RefreshRateAsync();
    }

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

    // ----- currency and exchange rate -----

    /// <summary>"12,50 EUR" typed into the amount box switches the currency.</summary>
    partial void OnAmountTextChanged(string value)
    {
        if (AmountParser.TryParseWithCurrency(value, CurrencyCatalog.Codes, out _, out var typed)
            && typed is not null && !typed.Equals(CurrencyCode, StringComparison.OrdinalIgnoreCase))
            CurrencyCode = typed;
        UpdateConverted();
    }

    partial void OnCurrencyCodeChanged(string value)
    {
        _rateManual = false;
        _ = RefreshRateAsync();
        UpdateConverted();
    }

    partial void OnRateTextChanged(string value)
    {
        if (!_applyingRate)
        {
            _rateManual = true;
            _rateDate = null;
            RateInfo = Res.Get("RateManual");
        }
        UpdateConverted();
    }

    private async Task RefreshRateAsync()
    {
        _rateCts?.Cancel();
        if (!IsForeign)
        {
            _rateBusy = false;
            SetRate("", "");
            return;
        }

        var cts = new CancellationTokenSource();
        _rateCts = cts;
        var code = CurrencyCode;
        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        _rateBusy = true;
        RateInfo = Res.Get("RateLoading");
        try
        {
            var lookup = await _core.Rates.GetRateAsync(code, date, cts.Token);
            if (cts.IsCancellationRequested) return;

            _rateManual = false;
            _rateDate = lookup.EffectiveDate;
            var when = lookup.EffectiveDate?.ToString("d", CultureInfo.CurrentCulture) ?? "";
            var info = lookup.Origin switch
            {
                RateOrigin.Nbp => Res.Format("RateNbp", when),
                RateOrigin.Cache => Res.Format("RateCached", when),
                RateOrigin.LastKnown => Res.Format("RateLastKnown", when),
                _ => Res.Get("RateNone"),
            };
            SetRate(lookup.Rate?.ToString("0.####", CultureInfo.CurrentCulture) ?? "", info);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer lookup
        }
        finally
        {
            if (ReferenceEquals(_rateCts, cts)) _rateBusy = false;
        }
    }

    private void SetRate(string text, string info)
    {
        _applyingRate = true;
        RateText = text;
        _applyingRate = false;
        RateInfo = info;
        UpdateConverted();
    }

    [RelayCommand]
    private void ResetRate()
    {
        _rateManual = false;
        _ = RefreshRateAsync();
    }

    private void UpdateConverted()
    {
        if (IsForeign
            && AmountParser.TryParseWithCurrency(AmountText, CurrencyCatalog.Codes, out var amount, out _)
            && AmountParser.TryParseRate(RateText, out var rate))
            ConvertedText = "= " + Money.ToPln(amount, rate).ToString("C2", CultureInfo.CurrentCulture);
        else
            ConvertedText = "";
    }

    // ----- commands -----

    [RelayCommand]
    private void ToggleType() => IsIncome = !IsIncome;

    [RelayCommand]
    public void Add()
    {
        ErrorMessage = "";
        if (!AmountParser.TryParseWithCurrency(AmountText, CurrencyCatalog.Codes, out var amount, out _) || amount <= 0)
        {
            ErrorMessage = Res.Get("QuickAddBadAmount");
            AmountRejected?.Invoke();
            return;
        }

        var rate = 1m;
        if (IsForeign)
        {
            if (_rateBusy)
            {
                ErrorMessage = Res.Get("RateWait");
                return;
            }
            if (!AmountParser.TryParseRate(RateText, out rate))
            {
                ErrorMessage = Res.Get("RateMissing");
                return;
            }
        }

        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        var eventId = EventKey == Guid.Empty ? (Guid?)null : EventKey;
        var type = IsIncome ? TransactionType.Income : TransactionType.Expense;
        var categoryName = Categories.FirstOrDefault(c => c.Id == CategoryId)?.DisplayName;
        var code = CurrencyCode;

        Transaction saved;
        try
        {
            saved = _core.Transactions.Add(date, type, amount, CategoryId, Note, eventId, place: Place,
                currency: code, rateToPln: rate, rateDate: _rateManual ? null : _rateDate, rateManual: _rateManual);
            if (CategoryId is { } cat && !string.IsNullOrWhiteSpace(Place))
                _core.Rules.Learn(Place, cat, _eventSource is EventSource.Manual or EventSource.Rule ? eventId : null);
        }
        catch (UpdateRequiredException)
        {
            ErrorMessage = Res.Get("UpdateRequiredShort");
            return;
        }

        var shown = IsForeign
            ? $"{amount.ToString("N2", CultureInfo.CurrentCulture)} {code} ({saved.AmountPln.ToString("C2", CultureInfo.CurrentCulture)})"
            : amount.ToString("C2", CultureInfo.CurrentCulture);
        _toast.Show(Res.Format("QuickAddSaved", shown + (categoryName is null ? "" : " · " + categoryName)),
            () => _core.Transactions.Delete([saved]));
        ClearFields();
        Saved?.Invoke();
    }

    private void ClearFields()
    {
        _applying = true;
        AmountText = "";
        Place = "";
        Note = "";
        CategoryId = null;
        IsIncome = false;
        _applying = false;
        _categoryManual = false;
        _eventSource = EventSource.None;
        ApplyDefaultEvent();
        // the currency (and its rate) stays selected: several purchases abroad in a row are typical
    }

    [RelayCommand]
    private void Clear()
    {
        ClearFields();
        ErrorMessage = "";
        Saved?.Invoke();
    }
}
