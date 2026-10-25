using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed record PlaceRow(string Name, int Count, decimal Amount, double Share, string Key = "");

/// <summary>One entry counted in the total of a place: only to look at.</summary>
public sealed record PlaceTransactionRow(DateTime Date, decimal Amount, string Category, string Note);

/// <summary>Totals per place (shop, company…) in a chosen period.</summary>
public sealed partial class PlacesViewModel : ObservableObject
{
    private readonly BudgetCore _core;

    public ObservableCollection<PlaceRow> Rows { get; } = [];

    /// <summary>The entries counted in the total of the selected place.</summary>
    public ObservableCollection<PlaceTransactionRow> Details { get; } = [];

    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIncome))] private bool _isExpense = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private PlaceRow? _selectedRow;
    [ObservableProperty] private string _detailsTitle = "";
    [ObservableProperty] private bool _hasDetails;

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

    partial void OnSelectedRowChanged(PlaceRow? value) => LoadDetails();

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
        var keep = SelectedRow?.Key;
        Rows.Clear();
        var from = From is { } f ? DateOnly.FromDateTime(f) : DateOnly.MinValue;
        var to = To is { } t ? DateOnly.FromDateTime(t) : DateOnly.MaxValue;
        var type = IsExpense ? TransactionType.Expense : TransactionType.Income;
        var places = ComparisonReports.ByPlace(_core.Reader.GetTransactions(), from, to, type, Res.Get("PlacesNone"));

        var query = Search.Trim();
        if (query.Length > 0)
            places = places.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        foreach (var p in places) Rows.Add(new PlaceRow(p.Name, p.Count, p.Amount, (double)p.Share, p.Key));
        HasRows = Rows.Count > 0;
        Summary = Res.Format("PlacesSummary", Rows.Sum(r => r.Amount).ToString("C2", CultureInfo.CurrentCulture), Rows.Count, Rows.Sum(r => r.Count));

        // the same place stays selected after the period or the list changed (its entries are shown again)
        var again = keep is null ? null : Rows.FirstOrDefault(r => r.Key == keep);
        if (ReferenceEquals(again, SelectedRow)) LoadDetails();
        else SelectedRow = again;
    }

    /// <summary>The entries that make up the selected place's total, with the same period and type as the list.</summary>
    private void LoadDetails()
    {
        Details.Clear();
        if (SelectedRow is not { } row)
        {
            DetailsTitle = "";
            HasDetails = false;
            return;
        }

        var from = From is { } f ? DateOnly.FromDateTime(f) : DateOnly.MinValue;
        var to = To is { } t ? DateOnly.FromDateTime(t) : DateOnly.MaxValue;
        var type = IsExpense ? TransactionType.Expense : TransactionType.Income;
        var categories = _core.Reader.GetCategories().ToDictionary(c => c.Id);
        foreach (var tx in ComparisonReports.OfPlace(_core.Reader.GetTransactions(), from, to, type, row.Key))
        {
            var category = CategoryHelpers.PathOf(tx.CategoryId, categories);
            Details.Add(new PlaceTransactionRow(tx.Date.ToDateTime(TimeOnly.MinValue), tx.AmountPln, category.Length > 0 ? category : Res.Get("PlacesNoCategory"), tx.Note));
        }
        DetailsTitle = Res.Format("PlacesDetailsTitle", row.Name, Details.Count);
        HasDetails = true;
    }
}
