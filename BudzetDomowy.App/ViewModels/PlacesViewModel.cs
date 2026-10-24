using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record PlaceRow(string Name, int Count, decimal Amount, double Share);

/// <summary>Totals per place (shop, company…) in a chosen period.</summary>
public sealed partial class PlacesViewModel : ObservableObject
{
    private readonly BudgetCore _core;

    public ObservableCollection<PlaceRow> Rows { get; } = [];

    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIncome))] private bool _isExpense = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasRows;

    public bool IsIncome => !IsExpense;

    public PlacesViewModel(BudgetCore core)
    {
        _core = core;
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        SetPeriod(DateOnly.FromDateTime(first), DateOnly.FromDateTime(first.AddMonths(1).AddDays(-1)));
    }

    partial void OnFromChanged(DateTime? value) => Reload();
    partial void OnToChanged(DateTime? value) => Reload();
    partial void OnIsExpenseChanged(bool value) => Reload();
    partial void OnSearchChanged(string value) => Reload();

    [RelayCommand]
    private void SetType(string? key) => IsExpense = key != "Income";

    /// <summary>Follows the period chosen at the top of the reports (the dates can still be changed here).</summary>
    public void SetPeriod(DateOnly from, DateOnly to)
    {
        From = from.ToDateTime(TimeOnly.MinValue);
        To = to.ToDateTime(TimeOnly.MinValue);
    }

    public void Reload()
    {
        Rows.Clear();
        var from = From is { } f ? DateOnly.FromDateTime(f) : DateOnly.MinValue;
        var to = To is { } t ? DateOnly.FromDateTime(t) : DateOnly.MaxValue;
        var type = IsExpense ? TransactionType.Expense : TransactionType.Income;
        var places = ComparisonReports.ByPlace(_core.Reader.GetTransactions(), from, to, type, Res.Get("PlacesNone"));

        var query = Search.Trim();
        if (query.Length > 0)
            places = places.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        foreach (var p in places) Rows.Add(new PlaceRow(p.Name, p.Count, p.Amount, (double)p.Share));
        HasRows = Rows.Count > 0;
        Summary = Res.Format("PlacesSummary", Rows.Sum(r => r.Amount).ToString("C2", CultureInfo.CurrentCulture), Rows.Count, Rows.Sum(r => r.Count));
    }
}
