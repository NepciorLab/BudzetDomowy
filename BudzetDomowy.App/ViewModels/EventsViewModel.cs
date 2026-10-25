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
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BudzetDomowy.App.ViewModels;

public sealed record EventListItem(SpecialEvent Model, string Dates, string Status, string Spent)
{
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;
}

public sealed record SliceView(string Name, string Color, string AmountText, string ShareText);

public sealed record EventTxRow(DateTime Date, string AmountDisplay, bool IsIncome, string Category, string Place, string Note);

public sealed partial class EventsViewModel : ObservableObject
{
    private static readonly string[] Palette =
        ["#1E88E5", "#43A047", "#FB8C00", "#8E24AA", "#E53935", "#00ACC1", "#FFB300", "#5C6BC0", "#EC407A", "#7CB342"];

    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private bool _reloading;

    public ObservableCollection<EventListItem> Items { get; } = [];
    public IReadOnlyList<string> ColorPalette => Palette;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private EventListItem? _selected;

    // editor
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private DateTime? _editFrom;
    [ObservableProperty] private DateTime? _editTo;
    [ObservableProperty] private string _editPlanned = "";
    [ObservableProperty] private string _editColor = "";
    [ObservableProperty] private string _editNote = "";
    [ObservableProperty] private string _message = "";

    // report
    [ObservableProperty] private string _expenseText = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _daysText = "";
    [ObservableProperty] private string _perDayText = "";
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private double _planFraction;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _overPlan;
    [ObservableProperty] private IReadOnlyList<SliceView> _slices = [];
    [ObservableProperty] private IReadOnlyList<EventTxRow> _transactions = [];
    [ObservableProperty] private IEnumerable<ISeries> _pieSeries = [];
    [ObservableProperty] private bool _hasSlices;

    public bool HasSelection => Selected is not null;

    public EventsViewModel(BudgetCore core, DataHub hub, ToastService toast)
    {
        _core = core;
        _toast = toast;
        UiPreferences.Instance.OnAppearanceChanged(this, self => self.Reload());
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnSelectedChanged(EventListItem? value)
    {
        if (_reloading) return;
        LoadEditor(value);
        BuildReport();
    }

    public void Reload()
    {
        var keep = Selected?.Id;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var events = _core.Reader.GetSpecialEvents().OrderByDescending(e => e.DateFrom).ToList();
        var spent = _core.Reader.GetTransactions()
            .Where(t => t.EventId is not null && t.Type == TransactionType.Expense)
            .GroupBy(t => t.EventId!.Value).ToDictionary(g => g.Key, g => g.Sum(t => t.AmountPln));

        _reloading = true;
        try
        {
            Items.Clear();
            foreach (var e in events)
            {
                var status = today < e.DateFrom ? Res.Get("EventPlanned") : today > e.DateTo ? Res.Get("EventFinished") : Res.Get("EventOngoing");
                Items.Add(new EventListItem(e, DateRange(e), status, spent.GetValueOrDefault(e.Id).ToString("C2", CultureInfo.CurrentCulture)));
            }
            Selected = keep is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
        }
        finally
        {
            _reloading = false;
        }
        if (Selected is not null) BuildReport();
        else ClearReport();
    }

    private static string DateRange(SpecialEvent e) =>
        e.DateFrom == e.DateTo
            ? e.DateFrom.ToString("d", CultureInfo.CurrentCulture)
            : $"{e.DateFrom.ToString("d", CultureInfo.CurrentCulture)} – {e.DateTo.ToString("d", CultureInfo.CurrentCulture)}";

    private void LoadEditor(EventListItem? item)
    {
        Message = "";
        if (item is null) return;
        var e = item.Model;
        EditName = e.Name;
        EditFrom = e.DateFrom.ToDateTime(TimeOnly.MinValue);
        EditTo = e.DateTo.ToDateTime(TimeOnly.MinValue);
        EditPlanned = e.PlannedBudgetPln is { } p ? p.ToString("0.##", CultureInfo.CurrentCulture) : "";
        EditColor = e.Color;
        EditNote = e.Note;
    }

    private void ClearReport()
    {
        Slices = [];
        Transactions = [];
        PieSeries = [];
        HasSlices = false;
        HasPlan = false;
    }

    private void BuildReport()
    {
        if (Selected is not { } item)
        {
            ClearReport();
            return;
        }

        var cats = _core.Reader.GetCategories();
        var byId = cats.ToDictionary(c => c.Id);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var r = Reports.ForEvent(item.Model, _core.Reader.GetTransactions(), cats, today);
        string Money(decimal v) => v.ToString("C2", CultureInfo.CurrentCulture);

        ExpenseText = Money(r.ExpensePln);
        IncomeText = Money(r.IncomePln);
        DaysText = r.Days.ToString(CultureInfo.CurrentCulture);
        PerDayText = r.ExpensePerDay is { } d ? Money(d) : "—";

        HasPlan = r.PlannedPln is not null;
        OverPlan = r.RemainingPln is < 0;
        PlanFraction = (double)Math.Min(1m, r.PlannedUsedFraction ?? 0m);
        PlanText = r.PlannedPln is { } plan
            ? Res.Format("EventPlanText", Money(r.ExpensePln), Money(plan), ((r.PlannedUsedFraction ?? 0m) * 100m).ToString("0", CultureInfo.CurrentCulture))
            : Res.Get("EventNoPlan");
        RemainingText = r.RemainingPln is { } rem
            ? (rem >= 0 ? Res.Format("EventRemaining", Money(rem)) : Res.Format("EventOver", Money(-rem)))
            : "";

        var uncategorized = Res.Get("NoCategory");
        var slices = r.ByCategory.Select((s, i) => new
        {
            s,
            Name = s.Name.Length == 0 ? uncategorized : s.Name,
            Color = s.Color.Length == 0 ? Palette[i % Palette.Length] : s.Color,
        }).ToList();
        Slices = slices.Select(x => new SliceView(x.Name, x.Color, Money(x.s.AmountPln), (x.s.Share * 100m).ToString("0.#", CultureInfo.CurrentCulture) + "%")).ToList();
        HasSlices = slices.Count > 0;
        PieSeries = slices.Select(x => (ISeries)new PieSeries<double>
        {
            Values = [(double)x.s.AmountPln],
            Name = x.Name,
            InnerRadius = 70,
            Fill = new SolidColorPaint(SKColor.TryParse(x.Color, out var c) ? c : SKColors.Gray),
            Stroke = null,
            ToolTipLabelFormatter = ChartFactory.MoneyTip,
        }).ToList();

        Transactions = r.Transactions.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).Select(t => new EventTxRow(
            t.Date.ToDateTime(TimeOnly.MinValue),
            UiPreferences.Instance.SignOf(t.Type == TransactionType.Income) + Money(t.AmountPln) + (CurrencyIsForeign(t) ? $"  ({t.Amount.ToString("N2", CultureInfo.CurrentCulture)} {t.CurrencyCode})" : ""),
            t.Type == TransactionType.Income,
            CategoryHelpers.PathOf(t.CategoryId, byId), t.Place, t.Note)).ToList();
    }

    private static bool CurrencyIsForeign(Transaction t) => !t.CurrencyCode.Equals("PLN", StringComparison.OrdinalIgnoreCase);

    // ----- commands -----

    [RelayCommand]
    private void AddEvent()
    {
        Run(() =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var color = Palette[_core.Reader.GetSpecialEvents().Count % Palette.Length];
            var e = _core.Events.Add(Res.Get("NewEventName"), today, today.AddDays(6), color);
            Reload();
            Selected = Items.FirstOrDefault(i => i.Id == e.Id);
        });
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is not { } item) return;
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            Message = Res.Get("EventNameRequired");
            return;
        }
        if (EditFrom is not { } from || EditTo is not { } to)
        {
            Message = Res.Get("EventDatesRequired");
            return;
        }

        decimal? planned = null;
        if (!string.IsNullOrWhiteSpace(EditPlanned))
        {
            if (!AmountParser.TryParse(EditPlanned, out var p))
            {
                Message = Res.Get("EventPlanInvalid");
                return;
            }
            planned = p > 0 ? p : null;
        }

        var color = EditColor.Trim();
        Run(() =>
        {
            var saved = _core.Events.Save(item.Model with
            {
                Name = name,
                DateFrom = DateOnly.FromDateTime(from),
                DateTo = DateOnly.FromDateTime(to),
                PlannedBudgetPln = planned,
                Color = color,
                Note = EditNote.Trim(),
            });
            Reload();
            Selected = Items.FirstOrDefault(i => i.Id == saved.Id);
            Message = Res.Get("EventSaved");
        });
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } item) return;
        Run(() =>
        {
            var deletion = _core.Events.Delete(item.Id);
            Selected = null;
            Reload();
            _toast.Show(Res.Format("EventDeleted", item.Name), () => _core.Events.Restore(deletion));
        });
    }

    [RelayCommand]
    private void PickColor(string? hex)
    {
        if (hex is not null) EditColor = hex;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (UpdateRequiredException) { Message = Res.Get("UpdateRequiredShort"); }
    }
}
