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

public sealed partial class BudgetRowViewModel : ObservableObject
{
    private readonly BudgetsViewModel _owner;

    public Guid CategoryId { get; }
    public string Name { get; }
    public double IndentWidth { get; }
    public string Color { get; }

    [ObservableProperty] private string _spentText = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _status = "Ok";
    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private bool _hasLimit;

    // the yearly limit and what was spent in the calendar year
    [ObservableProperty] private string _yearSpentText = "";
    [ObservableProperty] private double _yearFraction;
    [ObservableProperty] private string _yearPercentText = "";
    [ObservableProperty] private string _yearStatus = "Ok";
    [ObservableProperty] private bool _hasYearLimit;

    private string _limitText = "";
    private string _yearLimitText = "";

    public BudgetRowViewModel(BudgetsViewModel owner, CategoryOption option)
    {
        _owner = owner;
        CategoryId = option.Id;
        Name = option.Category.Name;
        IndentWidth = option.Depth * 22;
        Color = option.Category.Color;
    }

    /// <summary>The monthly limit as typed; the change applies from the shown month on.</summary>
    public string LimitText
    {
        get => _limitText;
        set
        {
            var text = (value ?? "").Trim();
            if (text == _limitText) return;
            decimal amount;
            if (text.Length == 0) amount = 0;
            else if (!AmountParser.TryParse(text, out amount))
            {
                OnPropertyChanged(); // reject: show the old value again
                return;
            }
            _limitText = amount == 0 ? "" : amount.ToString("0.##", CultureInfo.CurrentCulture);
            OnPropertyChanged();
            _owner.SetLimit(this, amount);
        }
    }

    /// <summary>The yearly limit as typed; like the monthly one it applies from the shown month on.</summary>
    public string YearLimitText
    {
        get => _yearLimitText;
        set
        {
            var text = (value ?? "").Trim();
            if (text == _yearLimitText) return;
            decimal amount;
            if (text.Length == 0) amount = 0;
            else if (!AmountParser.TryParse(text, out amount))
            {
                OnPropertyChanged(); // reject: show the old value again
                return;
            }
            _yearLimitText = amount == 0 ? "" : amount.ToString("0.##", CultureInfo.CurrentCulture);
            OnPropertyChanged();
            _owner.SetYearLimit(this, amount);
        }
    }

    public void SetYearLimitFromModel(decimal limit)
    {
        _yearLimitText = limit > 0 ? limit.ToString("0.##", CultureInfo.CurrentCulture) : "";
        HasYearLimit = limit > 0;
        OnPropertyChanged(nameof(YearLimitText));
    }

    /// <summary>Takes the value from the model without saving it again.</summary>
    public void SetLimitFromModel(decimal limit)
    {
        _limitText = limit > 0 ? limit.ToString("0.##", CultureInfo.CurrentCulture) : "";
        HasLimit = limit > 0;
        OnPropertyChanged(nameof(LimitText));
    }
}

public sealed partial class BudgetsViewModel : ObservableObject, IPeriodNavigable, IRefreshOnShow
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly Dictionary<Guid, BudgetRowViewModel> _rowsById = [];

    public ObservableCollection<BudgetRowViewModel> Rows { get; } = [];

    [ObservableProperty] private DateOnly _month = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));
    [ObservableProperty] private string _monthTitle = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _message = "";

    private readonly DataHub _hub;

    public BudgetsViewModel(BudgetCore core, DataHub hub, ToastService toast)
    {
        _core = core;
        _toast = toast;
        _hub = hub;
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnMonthChanged(DateOnly value) => Reload();

    public void PreviousPeriod() => Month = Month.AddMonths(-1);
    public void NextPeriod() => Month = Month.AddMonths(1);

    [RelayCommand] private void PreviousMonth() => Month = Month.AddMonths(-1);
    [RelayCommand] private void NextMonth() => Month = Month.AddMonths(1);
    [RelayCommand] private void ThisMonth() => Month = BudgetMath.MonthStart(DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void CopyPrevious()
    {
        try
        {
            var count = _core.Budgets.CopyFromPreviousMonth(Month);
            Message = count == 0 ? Res.Get("BudgetsNothingToCopy") : Res.Format("BudgetsCopied", count);
            Reload();
            _hub.NotifyNow();                                                                // the Dashboard shows the limits too
        }
        catch (UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
        }
    }

    internal void SetLimit(BudgetRowViewModel row, decimal amount)
    {
        try
        {
            _core.Budgets.SetLimit(row.CategoryId, amount, Month);
            Message = "";
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
        }
        Reload();
        _hub.NotifyNow();                                                                    // the Dashboard shows the limits too: no wait, no restart
    }

    public void RefreshOnShow() => Reload();

    internal void SetYearLimit(BudgetRowViewModel row, decimal amount)
    {
        try
        {
            _core.Budgets.SetYearlyLimit(row.CategoryId, amount, Month);
            Message = "";
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
        }
        Reload();
        _hub.NotifyNow();                                                                    // the Dashboard shows the limits too
    }

    public void Reload()
    {
        var title = Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        MonthTitle = char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..];

        var budgets = _core.Reader.GetBudgets();
        var effective = BudgetMath.Effective(budgets, Month);
        var cats = _core.Reader.GetCategories();
        var effectiveYearly = BudgetMath.EffectiveYearly(budgets, Month);
        var transactions = _core.Reader.GetTransactions();
        var progress = BudgetMath.Progress(Month, budgets, cats, transactions).ToDictionary(l => l.Category.Id);
        var yearProgress = BudgetMath.YearlyProgress(Month, budgets, cats, transactions).ToDictionary(l => l.Category.Id);

        // expense categories that can take a limit; archived ones only while they still have one
        var options = CategoryHelpers.Flatten(cats, c =>
            c.Type != CategoryType.Income && (!c.IsArchived || effective.ContainsKey(c.Id) || effectiveYearly.ContainsKey(c.Id)));

        var wanted = options.Select(o => o.Id).ToList();
        if (!wanted.SequenceEqual(Rows.Select(r => r.CategoryId)))
        {
            Rows.Clear();
            _rowsById.Clear();
            foreach (var o in options)
            {
                var row = new BudgetRowViewModel(this, o);
                _rowsById[o.Id] = row;
                Rows.Add(row);
            }
        }

        string Money(decimal v) => v.ToString("C0", CultureInfo.CurrentCulture);
        decimal totalLimit = 0, totalYearly = 0;
        foreach (var row in Rows)
        {
            var limit = effective.GetValueOrDefault(row.CategoryId);
            row.SetLimitFromModel(limit);
            var yearly = effectiveYearly.GetValueOrDefault(row.CategoryId);
            row.SetYearLimitFromModel(yearly);
            if (yearProgress.TryGetValue(row.CategoryId, out var year))
            {
                row.YearSpentText = Res.Format("BudgetsSpentOf", Money(year.Spent));
                row.YearFraction = (double)Math.Min(1m, year.Fraction);
                row.YearPercentText = (year.Fraction * 100m).ToString("0", CultureInfo.CurrentCulture) + "%";
                row.YearStatus = year.Status.ToString();
            }
            else
            {
                row.YearSpentText = "";
                row.YearFraction = 0;
                row.YearPercentText = "";
                row.YearStatus = "Ok";
            }
            if (progress.TryGetValue(row.CategoryId, out var line))
            {
                row.SpentText = Res.Format("BudgetsSpentOf", Money(line.Spent));
                row.Fraction = (double)Math.Min(1m, line.Fraction);
                row.PercentText = (line.Fraction * 100m).ToString("0", CultureInfo.CurrentCulture) + "%";
                row.Status = line.Status.ToString();
            }
            else
            {
                row.SpentText = "";
                row.Fraction = 0;
                row.PercentText = "";
                row.Status = "Ok";
            }
            row.HistoryText = HistoryOf(row.CategoryId, budgets);
            var isMain = cats.FirstOrDefault(c => c.Id == row.CategoryId)?.ParentId is null;
            if (limit > 0 && isMain) totalLimit += limit;
            if (yearly > 0 && isMain) totalYearly += yearly;
        }
        TotalText = string.Join("   ", new[]
        {
            totalLimit > 0 ? Res.Format("BudgetsTotal", Money(totalLimit)) : "",
            totalYearly > 0 ? Res.Format("BudgetsTotalYear", Money(totalYearly)) : "",
        }.Where(t => t.Length > 0));
    }

    private static string HistoryOf(Guid categoryId, IEnumerable<Budget> budgets)
    {
        var parts = budgets.Where(b => b.CategoryId == categoryId).OrderBy(b => b.ValidFromMonth)
            .Select(b =>
            {
                var from = b.ValidFromMonth.ToString("MM.yyyy", CultureInfo.CurrentCulture);
                var month = b.MonthlyAmount.ToString("C0", CultureInfo.CurrentCulture);
                var year = b.YearlyAmount.ToString("C0", CultureInfo.CurrentCulture);
                return b.MonthlyAmount > 0 && b.YearlyAmount > 0 ? Res.Format("BudgetsHistoryBoth", from, month, year)
                    : b.MonthlyAmount > 0 ? Res.Format("BudgetsHistoryItem", from, month)
                    : b.YearlyAmount > 0 ? Res.Format("BudgetsHistoryYearly", from, year)
                    : Res.Format("BudgetsHistoryOff", from);
            })
            .ToList();
        return parts.Count == 0 ? "" : Res.Get("BudgetsHistory") + " " + string.Join(" · ", parts);
    }
}
