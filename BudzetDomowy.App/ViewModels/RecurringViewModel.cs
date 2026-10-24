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

public sealed record FrequencyOption(RecurrenceFrequency Frequency, string Label)
{
    public override string ToString() => Label;
}

public sealed record WeekdayOption(int Day, string Label)
{
    public override string ToString() => Label;
}

public sealed record RecurringItem(RecurringTransaction Model, string Title, string Schedule, string AmountText, string LastText)
{
    public Guid Id => Model.Id;
}

public sealed partial class RecurringViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly RecurringRunner _runner;

    public ObservableCollection<RecurringItem> Items { get; } = [];

    public IReadOnlyList<FrequencyOption> Frequencies { get; } =
    [
        new(RecurrenceFrequency.Weekly, Res.Get("RecurWeekly")),
        new(RecurrenceFrequency.Monthly, Res.Get("RecurMonthly")),
        new(RecurrenceFrequency.Yearly, Res.Get("RecurYearly")),
    ];

    public IReadOnlyList<WeekdayOption> Weekdays { get; } = Enumerable.Range(1, 7)
        .Select(i => new WeekdayOption(i, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(i == 7 ? DayOfWeek.Sunday : (DayOfWeek)i))).ToList();

    public IReadOnlyList<int> MonthDays { get; } = Enumerable.Range(1, 31).ToList();
    public IReadOnlyList<CurrencyOption> Currencies { get; } = CurrencyCatalog.Ordered([]);

    [ObservableProperty] private IReadOnlyList<CategoryOption> _categories = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private RecurringItem? _selected;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExpense))] private bool _editIsIncome;
    [ObservableProperty] private string _editAmount = "";
    [ObservableProperty] private string _editCurrency = CurrencyCatalog.Pln;
    [ObservableProperty] private Guid? _editCategoryId;
    [ObservableProperty] private string _editPlace = "";
    [ObservableProperty] private string _editNote = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWeekly))] private FrequencyOption? _editFrequency;
    [ObservableProperty] private int _editWeekday = 1;
    [ObservableProperty] private int _editMonthDay = 1;
    [ObservableProperty] private DateTime? _editStart = DateTime.Today;
    [ObservableProperty] private DateTime? _editEnd;
    [ObservableProperty] private bool _editConfirm;
    [ObservableProperty] private string _message = "";

    private bool _loading;

    public bool HasSelection => Selected is not null;
    public bool IsExpense => !EditIsIncome;
    public bool IsWeekly => EditFrequency?.Frequency == RecurrenceFrequency.Weekly;

    public RecurringViewModel(BudgetCore core, DataHub hub, ToastService toast, RecurringRunner runner)
    {
        _core = core;
        _toast = toast;
        _runner = runner;
        _editFrequency = Frequencies[1];
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnSelectedChanged(RecurringItem? value)
    {
        if (!_loading) LoadEditor(value);
    }

    partial void OnEditIsIncomeChanged(bool value) => ReloadCategories();

    private void ReloadCategories()
    {
        var keep = EditCategoryId;
        Categories = CategoryHelpers.ForEntry(_core.Reader.GetCategories(), EditIsIncome ? TransactionType.Income : TransactionType.Expense);
        if (keep is { } id && Categories.All(c => c.Id != id)) EditCategoryId = null;
    }

    public void Reload()
    {
        var keep = Selected?.Id;
        _loading = true;
        try
        {
            ReloadCategories();
            var cats = _core.Reader.GetCategories().ToDictionary(c => c.Id);
            Items.Clear();
            foreach (var t in _core.Reader.GetRecurringTransactions())
            {
                var title = t.Place.Length > 0 ? t.Place : t.Note.Length > 0 ? t.Note : CategoryHelpers.PathOf(t.CategoryId, cats);
                Items.Add(new RecurringItem(t, title.Length > 0 ? title : Res.Get("RecurUntitled"), Schedule(t),
                    (t.Type == TransactionType.Income ? "+" : "−") + t.Amount.ToString("N2", CultureInfo.CurrentCulture) + " " + t.CurrencyCode,
                    t.LastGeneratedDate is { } d ? Res.Format("RecurLast", d.ToString("d", CultureInfo.CurrentCulture)) : Res.Get("RecurNotYet")));
            }
            Selected = keep is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
        }
        finally
        {
            _loading = false;
        }
    }

    private string Schedule(RecurringTransaction t)
    {
        var from = t.StartDate.ToString("d", CultureInfo.CurrentCulture);
        var until = t.EndDate is { } e ? " " + Res.Format("RecurUntil", e.ToString("d", CultureInfo.CurrentCulture)) : "";
        return t.Frequency switch
        {
            RecurrenceFrequency.Weekly => Res.Format("RecurEveryWeek", Weekdays.First(w => w.Day == Math.Clamp(t.Day, 1, 7)).Label, from) + until,
            RecurrenceFrequency.Monthly => Res.Format("RecurEveryMonth", t.Day, from) + until,
            _ => Res.Format("RecurEveryYear", t.Day, CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(t.StartDate.Month), from) + until,
        };
    }

    private void LoadEditor(RecurringItem? item)
    {
        Message = "";
        if (item is null) return;
        var t = item.Model;
        EditIsIncome = t.Type == TransactionType.Income;
        EditAmount = t.Amount.ToString("0.##", CultureInfo.CurrentCulture);
        EditCurrency = t.CurrencyCode;
        EditCategoryId = t.CategoryId;
        EditPlace = t.Place;
        EditNote = t.Note;
        EditFrequency = Frequencies.First(f => f.Frequency == t.Frequency);
        if (t.Frequency == RecurrenceFrequency.Weekly) EditWeekday = Math.Clamp(t.Day, 1, 7); else EditMonthDay = Math.Clamp(t.Day, 1, 31);
        EditStart = t.StartDate.ToDateTime(TimeOnly.MinValue);
        EditEnd = t.EndDate?.ToDateTime(TimeOnly.MinValue);
        EditConfirm = t.ConfirmEach;
    }

    [RelayCommand]
    private void New()
    {
        _loading = true;
        Selected = null;
        _loading = false;
        EditIsIncome = false;
        EditAmount = "";
        EditCurrency = CurrencyCatalog.Pln;
        EditCategoryId = null;
        EditPlace = "";
        EditNote = "";
        EditFrequency = Frequencies[1];
        EditMonthDay = DateTime.Today.Day;
        EditStart = DateTime.Today;
        EditEnd = null;
        EditConfirm = false;
        Message = Res.Get("RecurFillIn");
    }

    [RelayCommand]
    private void Save()
    {
        if (!AmountParser.TryParse(EditAmount, out var amount) || amount <= 0)
        {
            Message = Res.Get("QuickAddBadAmount");
            return;
        }
        if (EditStart is not { } start)
        {
            Message = Res.Get("EventDatesRequired");
            return;
        }
        if (EditEnd is { } end && end < start)
        {
            Message = Res.Get("RecurEndBeforeStart");
            return;
        }

        var frequency = EditFrequency?.Frequency ?? RecurrenceFrequency.Monthly;
        var template = (Selected?.Model ?? new RecurringTransaction()) with
        {
            Type = EditIsIncome ? TransactionType.Income : TransactionType.Expense,
            Amount = amount,
            CurrencyCode = EditCurrency,
            CategoryId = EditCategoryId,
            Place = EditPlace,
            Note = EditNote,
            Frequency = frequency,
            Day = frequency == RecurrenceFrequency.Weekly ? EditWeekday : EditMonthDay,
            StartDate = DateOnly.FromDateTime(start),
            EndDate = EditEnd is { } e ? DateOnly.FromDateTime(e) : null,
            ConfirmEach = EditConfirm,
        };

        try
        {
            var saved = Selected is null ? _core.Recurring.Add(template) : Save(template);
            Reload();
            Selected = Items.FirstOrDefault(i => i.Id == saved.Id);
            Message = Res.Get("EventSaved");
        }
        catch (UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
        }
    }

    private RecurringTransaction Save(RecurringTransaction template)
    {
        _core.Recurring.Save(template);
        return template;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } item) return;
        var model = item.Model;
        try
        {
            _core.Recurring.Delete(model.Id);
        }
        catch (UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
            return;
        }
        Selected = null;
        Reload();
        _toast.Show(Res.Format("RecurDeleted", item.Title), () => _core.Recurring.Save(model));
    }

    [RelayCommand]
    private async Task GenerateNowAsync()
    {
        try
        {
            var run = await _runner.RunAsync(DateOnly.FromDateTime(DateTime.Today), askAgain: true);
            Message = run.Created > 0 ? Res.Format("RecurGenerated", run.Created) : run.Postponed > 0 ? "" : Res.Get("RecurNothingDue");
            if (run.Postponed > 0) Message = (Message + " " + Res.Format("RecurPostponed", run.Postponed)).Trim();
            if (run.WaitingForRate > 0) Message += " " + Res.Get("RecurWaitingForRate");
        }
        catch (UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
        }
    }
}
