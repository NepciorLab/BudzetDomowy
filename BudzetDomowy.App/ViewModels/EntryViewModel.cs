using System.Collections.ObjectModel;
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

public enum EntryFocusTarget { Total, LineCategory, LineAmount, Confirm }

/// <summary>Where the keyboard goes after Enter in an amount: the window moves the focus there.</summary>
public sealed record EntryFocus(EntryFocusTarget Target, EntryLineViewModel? Line = null);

/// <summary>One item of the receipt: a category and its amount (a number, a calculation like "2+2*2", or "r" for the remainder).</summary>
public sealed partial class EntryLineViewModel : ObservableObject
{
    private readonly EntryViewModel _owner;
    private string _amountExpression = "";
    private bool _settingCategory;

    public EntryLineViewModel(EntryViewModel owner) => _owner = owner;

    [ObservableProperty] private Guid? _categoryId;

    /// <summary>The user chose this category (a suggestion never overrides it).</summary>
    public bool CategoryChosen { get; private set; }

    /// <summary>What was typed. Changing it by hand takes the line out of the automatic filling.</summary>
    public string AmountExpression
    {
        get => _amountExpression;
        set
        {
            value ??= "";
            if (value == _amountExpression) return;
            _amountExpression = value;
            IsAuto = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRemainder));
            _owner.Recalculate();
        }
    }

    /// <summary>The program filled the amount in (the rest still to be assigned); it stays that way until the user types.</summary>
    [ObservableProperty] private bool _isAuto;

    /// <summary>The calculated amount; null while empty or not understood.</summary>
    [ObservableProperty] private decimal? _value;

    /// <summary>What the box shows when it is not being edited: the result of a calculation or of "r"; empty means "show what was typed".</summary>
    [ObservableProperty] private string _display = "";

    [ObservableProperty] private bool _isInvalid;

    public bool IsRemainder => AmountExpressionIsRemainder;

    private bool AmountExpressionIsRemainder => Core.Services.AmountExpression.IsRemainder(_amountExpression);

    public int Number => _owner.Lines.IndexOf(this) + 1;

    public bool CanRemove => _owner.Lines.Count > 1;

    partial void OnCategoryIdChanged(Guid? value)
    {
        if (!_settingCategory) CategoryChosen = value is not null;
    }

    internal void Suggest(Guid? category)
    {
        _settingCategory = true;
        CategoryId = category;
        _settingCategory = false;
        CategoryChosen = false;
    }

    internal void SetAuto(string text)
    {
        _amountExpression = text;
        IsAuto = true;
        OnPropertyChanged(nameof(AmountExpression));
        OnPropertyChanged(nameof(IsRemainder));
    }

    internal void Renumber()
    {
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(CanRemove));
    }
}

/// <summary>
/// A new expense or income: the data of the whole receipt first (date, place, total, currency, event), then one line per category.
/// The first line starts with the whole total and the category the place was most often booked under; whenever a line takes less
/// than what is left, the next line appears with the rest and the next most frequent category. Each line becomes its own transaction.
/// </summary>
public sealed partial class EntryViewModel : ObservableObject
{
    private enum EventSource { None, Default, Rule, Manual }

    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly PlaceCategoryDictionary _dictionary;

    /// <summary>The places typed before, best first: offered while the place is typed.</summary>
    public IReadOnlyList<PlaceSuggestion> PlaceSuggestions { get; }
    private IReadOnlyList<CategoryRule> _rules = [];
    private IReadOnlyList<SpecialEvent> _specialEvents = [];
    private bool _applying;
    private bool _recalculating;
    private EventSource _eventSource = EventSource.None;

    private CancellationTokenSource? _rateCts;
    private bool _applyingRate;
    private bool _rateManual;
    private bool _rateBusy;
    private DateOnly? _rateDate;

    public bool IsIncome { get; }
    public string Title => Res.Get(IsIncome ? "EntryTitleIncome" : "EntryTitleExpense");
    public ObservableCollection<EntryLineViewModel> Lines { get; } = [];

    /// <summary>The only line of an income (it has no items): the window binds its category to it.</summary>
    public EntryLineViewModel Main => Lines[0];
    public IReadOnlyList<CategoryOption> Categories { get; }
    public IReadOnlyList<CurrencyOption> Currencies { get; }
    public IReadOnlyList<EventOption> Events { get; }

    [ObservableProperty] private DateTime? _date = DateTime.Today;
    [ObservableProperty] private string _place = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private Guid _eventKey = Guid.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForeign))] private string _currencyCode = CurrencyCatalog.Pln;
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _rateInfo = "";
    [ObservableProperty] private string _convertedText = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _totalInvalid;

    private string _totalText = "";

    /// <summary>The total as typed: a number, a calculation, optionally followed by a currency ("12,50 EUR").</summary>
    public string TotalText
    {
        get => _totalText;
        set
        {
            value ??= "";
            if (value == _totalText) return;
            _totalText = value;
            OnPropertyChanged();
            Recalculate();
        }
    }

    /// <summary>The calculated total; null while empty or not understood.</summary>
    [ObservableProperty] private decimal? _total;

    /// <summary>Shown in the total box when it is not being edited, if the text was a calculation.</summary>
    [ObservableProperty] private string _totalDisplay = "";

    public bool IsForeign => CurrencyCatalog.IsForeign(CurrencyCode);

    private readonly IDuplicatePrompt? _duplicates;

    /// <summary>Raised after the entry was saved: the window closes.</summary>
    public event Action? Saved;

    /// <param name="duplicates">Asked when an entry like the new one is already there (null: the entry is just saved).</param>
    public EntryViewModel(BudgetCore core, ToastService toast, bool isIncome, IDuplicatePrompt? duplicates = null)
    {
        _core = core;
        _toast = toast;
        _duplicates = duplicates;
        IsIncome = isIncome;
        var type = isIncome ? TransactionType.Income : TransactionType.Expense;

        _rules = core.Reader.GetCategoryRules();
        _specialEvents = core.Reader.GetSpecialEvents();
        Categories = CategoryHelpers.ForEntry(core.Reader.GetCategories(), type);
        Currencies = CurrencyCatalog.Ordered(core.Reader.GetRecentCurrencies());
        _dictionary = PlaceCategoryDictionary.Build(core.Reader.GetTransactions(), type, core.Reader.GetCategories());
        PlaceSuggestions = core.PlaceSuggestions.All(type);

        var options = new List<EventOption> { new(Guid.Empty, Res.Get("NoEvent")) };
        options.AddRange(_specialEvents.OrderByDescending(e => e.DateFrom).Select(e => new EventOption(e.Id, e.Name)));
        Events = options;
        if (!isIncome) ApplyDefaultEvent();                  // an income has no event

        var first = new EntryLineViewModel(this);
        first.SetAuto("");
        Lines.Add(first);
        Recalculate();
    }

    private static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    // ----- the lines -----

    /// <summary>Calculates everything from what was typed: the total, every line, the remainder line, the summary.</summary>
    public void Recalculate()
    {
        if (_recalculating) return;
        _recalculating = true;
        try
        {
            ErrorMessage = "";
            RecalculateTotal();

            // a single automatic line follows the total
            if (Lines.Count == 1 && Lines[0].IsAuto) Lines[0].SetAuto(Total is { } t ? Fmt(t) : "");

            decimal others = 0;
            foreach (var line in Lines.Where(l => !l.IsRemainder))
            {
                line.IsInvalid = false;
                line.Value = null;
                line.Display = "";
                if (string.IsNullOrWhiteSpace(line.AmountExpression)) continue;
                if (AmountExpression.TryEvaluate(line.AmountExpression, CurrencyCatalog.Codes, out var v, out _))
                {
                    line.Value = v;
                    if (!AmountExpression.IsPlainAmount(line.AmountExpression, CurrencyCatalog.Codes)) line.Display = Fmt(v);
                    others += v;
                }
                else
                {
                    line.IsInvalid = true;
                }
            }

            var remainderLines = Lines.Where(l => l.IsRemainder).ToList();
            for (var i = 0; i < remainderLines.Count; i++)
            {
                var line = remainderLines[i];
                line.IsInvalid = i > 0;                                     // only one line can take "the rest"
                line.Value = i == 0 && Total is { } total ? Math.Max(0, total - others) : null;
                line.Display = line.Value is { } rest ? Fmt(rest) : "";
            }
            if (remainderLines.Count > 1) ErrorMessage = Res.Get("EntryTwoRemainders");

            var allocated = Lines.Sum(l => l.Value ?? 0);
            Summary = Total is { } sum
                ? Res.Format("EntrySummary", Fmt(allocated), Fmt(sum), Fmt(sum - allocated))
                : "";
            UpdateConverted();
        }
        finally
        {
            _recalculating = false;
        }
    }

    private void RecalculateTotal()
    {
        var text = _totalText;
        Total = null;
        TotalDisplay = "";
        TotalInvalid = false;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (AmountExpression.TryEvaluate(text, CurrencyCatalog.Codes, out var value, out var typedCurrency) && value > 0)
        {
            Total = value;
            if (!AmountExpression.IsPlainAmount(text, CurrencyCatalog.Codes)) TotalDisplay = Fmt(value) + (typedCurrency is null ? "" : " " + typedCurrency);
            if (typedCurrency is not null && !typedCurrency.Equals(CurrencyCode, StringComparison.OrdinalIgnoreCase)) CurrencyCode = typedCurrency;
        }
        else
        {
            TotalInvalid = true;
        }
    }

    private decimal Remaining => (Total ?? 0) - Lines.Sum(l => l.Value ?? 0);

    private HashSet<Guid> UsedCategories(EntryLineViewModel? except = null) =>
        Lines.Where(l => l != except && l.CategoryId is not null).Select(l => l.CategoryId!.Value).ToHashSet();

    /// <summary>Adds a line; it gets what is left to assign and the most frequent category of the place that no line uses yet.</summary>
    public EntryLineViewModel AddLine()
    {
        var line = new EntryLineViewModel(this);
        var left = Remaining;
        line.Suggest(_dictionary.SuggestNext(Place, UsedCategories()));
        if (left > 0) line.SetAuto(Fmt(left)); else line.SetAuto("");
        Lines.Add(line);
        foreach (var l in Lines) l.Renumber();
        Recalculate();
        return line;
    }

    public void RemoveLine(EntryLineViewModel line)
    {
        if (Lines.Count <= 1) return;
        Lines.Remove(line);
        foreach (var l in Lines) l.Renumber();
        Recalculate();
    }

    [RelayCommand]
    private void AddEmptyLine() => AddLine();

    [RelayCommand]
    private void Remove(EntryLineViewModel? line)
    {
        if (line is not null) RemoveLine(line);
    }

    /// <summary>
    /// Enter in an amount: it is calculated, and when it is less than what the total asks for, the next line with the rest appears.
    /// When nothing is left, the keyboard goes to "confirm".
    /// </summary>
    public EntryFocus CommitLine(EntryLineViewModel line)
    {
        Recalculate();
        if (Total is null)
        {
            ErrorMessage = Res.Get("EntryTotalMissing");
            return new EntryFocus(EntryFocusTarget.Total);
        }
        if (line.IsInvalid || line.Value is not > 0)
        {
            ErrorMessage = Res.Format("EntryBadLine", line.Number);
            return new EntryFocus(EntryFocusTarget.LineAmount, line);
        }

        var left = Remaining;
        if (left < 0)
        {
            ErrorMessage = Res.Format("EntryOver", Fmt(Lines.Sum(l => l.Value ?? 0)), Fmt(Total.Value));
            return new EntryFocus(EntryFocusTarget.LineAmount, line);
        }
        if (left == 0) return new EntryFocus(EntryFocusTarget.Confirm);

        var index = Lines.IndexOf(line);
        if (index == Lines.Count - 1) return new EntryFocus(EntryFocusTarget.LineCategory, AddLine());
        return new EntryFocus(EntryFocusTarget.LineCategory, Lines[index + 1]);
    }

    // ----- suggestions and defaults -----

    /// <summary>The place decides the category of the first line (unless the user chose one) and may suggest an event.</summary>
    partial void OnPlaceChanged(string value)
    {
        if (_applying) return;
        var first = Lines[0];
        if (!first.CategoryChosen)
            first.Suggest(_dictionary.SuggestNext(value, UsedCategories(first)));

        var s = CategoryRuleService.Suggest(value, _rules);
        if (s?.EventId is { } ev && _eventSource is EventSource.None or EventSource.Default or EventSource.Rule && Events.Any(e => e.Key == ev))
        {
            _applying = true;
            EventKey = ev;
            _eventSource = EventSource.Rule;
            _applying = false;
        }
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

    /// <summary>While the date is inside an event's dates that event is suggested; choosing "no event" switches it off.</summary>
    private void ApplyDefaultEvent()
    {
        if (IsIncome || _eventSource is EventSource.Manual or EventSource.Rule) return;
        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        var active = SpecialEvents.ActiveOn(_specialEvents, date);
        _applying = true;
        EventKey = active?.Id ?? Guid.Empty;
        _eventSource = active is null ? EventSource.None : EventSource.Default;
        _applying = false;
    }

    // ----- currency and exchange rate -----

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

    private void UpdateConverted() =>
        ConvertedText = IsForeign && Total is { } total && AmountParser.TryParseRate(RateText, out var rate)
            ? "= " + Money.ToPln(total, rate).ToString("C2", CultureInfo.CurrentCulture)
            : "";

    // ----- saving -----

    /// <summary>Checks everything and saves one transaction per line. False (with <see cref="ErrorMessage"/>) when something is wrong.</summary>
    public bool Confirm()
    {
        Recalculate();
        ErrorMessage = "";

        if (Lines.Count(l => l.IsRemainder) > 1)
        {
            ErrorMessage = Res.Get("EntryTwoRemainders");
            return false;
        }
        if (Total is not { } total)
        {
            ErrorMessage = Res.Get(TotalInvalid ? "QuickAddBadAmount" : "EntryTotalMissing");
            return false;
        }

        // an untouched empty line left over from the "+" button is ignored
        var used = Lines.Where(l => !(Lines.Count > 1 && l.CategoryId is null && string.IsNullOrWhiteSpace(l.AmountExpression))).ToList();
        foreach (var line in used)
        {
            if (line.IsInvalid || line.Value is not > 0)
            {
                ErrorMessage = Res.Format("EntryBadLine", line.Number);
                return false;
            }
        }
        var sum = used.Sum(l => l.Value!.Value);
        if (sum != total)
        {
            ErrorMessage = Res.Format(sum > total ? "EntryOver" : "EntryMismatch", Fmt(sum), Fmt(total));
            return false;
        }

        var rate = 1m;
        if (IsForeign)
        {
            if (_rateBusy)
            {
                ErrorMessage = Res.Get("RateWait");
                return false;
            }
            if (!AmountParser.TryParseRate(RateText, out rate))
            {
                ErrorMessage = Res.Get("RateMissing");
                return false;
            }
        }

        var date = DateOnly.FromDateTime(Date ?? DateTime.Today);
        var eventId = EventKey == Guid.Empty ? (Guid?)null : EventKey;
        var type = IsIncome ? TransactionType.Income : TransactionType.Expense;

        // an entry like this (day, place, category, amount) is already there: the user decides
        var twins = DuplicateFinder.Find(_core.Reader.GetTransactions(), date, type, CurrencyCode, Place, used.Select(l => (l.CategoryId, l.Value!.Value)).ToList());
        var merge = false;
        if (twins.Count > 0 && _duplicates is not null)
        {
            var infos = twins.Select(m => new DuplicateInfo(m.Existing.Date, m.Existing.Place,
                Categories.FirstOrDefault(c => c.Id == used[m.LineIndex].CategoryId)?.DisplayName ?? Res.Get("NoCategory"), used[m.LineIndex].Value!.Value, m.Existing.CurrencyCode)).ToList();
            switch (_duplicates.Ask(infos))
            {
                case DuplicateChoice.Cancel:
                    return false;                                      // the window stays: the entry can still be changed
                case DuplicateChoice.Merge:
                    merge = true;
                    break;
            }
        }

        var saved = new List<Transaction>();
        var before = new Dictionary<Guid, Transaction>();              // the entries that were joined, as they were
        var joined = new Dictionary<Guid, Transaction>();              // and as they are now
        try
        {
            for (var i = 0; i < used.Count; i++)
            {
                var line = used[i];
                if (merge && twins.FirstOrDefault(m => m.LineIndex == i) is { } twin)
                {
                    var current = joined.GetValueOrDefault(twin.Existing.Id) ?? twin.Existing;
                    before.TryAdd(twin.Existing.Id, twin.Existing);
                    joined[twin.Existing.Id] = _core.Transactions.MergeInto(current, line.CategoryId, line.Value!.Value);
                }
                else
                {
                    saved.Add(_core.Transactions.Add(date, type, line.Value!.Value, line.CategoryId, Note, eventId, place: Place,
                        currency: CurrencyCode, rateToPln: rate, rateDate: _rateManual ? null : _rateDate, rateManual: _rateManual));
                }
                if (line.CategoryId is { } category && !string.IsNullOrWhiteSpace(Place))
                    _core.Rules.Learn(Place, category, _eventSource is EventSource.Manual or EventSource.Rule ? eventId : null);
            }
            if (!IsIncome) _core.PlaceSuggestions.Learn(Place);
        }
        catch (UpdateRequiredException)
        {
            if (saved.Count > 0) _core.Transactions.Delete(saved);
            if (before.Count > 0) _core.Transactions.Restore(before.Values);
            ErrorMessage = Res.Get("UpdateRequiredShort");
            return false;
        }

        var shown = IsForeign
            ? $"{total.ToString("N2", CultureInfo.CurrentCulture)} {CurrencyCode}" + (before.Count > 0 ? "" : $" ({saved.Sum(t => t.AmountPln).ToString("C2", CultureInfo.CurrentCulture)})")
            : total.ToString("C2", CultureInfo.CurrentCulture);
        var detail = used.Count == 1
            ? Categories.FirstOrDefault(c => c.Id == used[0].CategoryId)?.DisplayName
            : Res.Format("EntryItems", used.Count);
        _toast.Show(Res.Format(before.Count > 0 ? "DupMerged" : "QuickAddSaved", shown + (string.IsNullOrEmpty(detail) ? "" : " · " + detail)),
            () =>
            {
                _core.Transactions.Delete(saved);
                if (before.Count > 0) _core.Transactions.Restore(before.Values);
            });
        Saved?.Invoke();
        return true;
    }
}
