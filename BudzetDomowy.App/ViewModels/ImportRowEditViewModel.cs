using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

/// <summary>The window for correcting one row of the import preview (opened through this so that tests need no window).</summary>
public interface IImportRowEditor
{
    /// <summary>The corrections to apply; null when the user cancelled.</summary>
    ImportRowFix? Edit(ImportRow row, IReadOnlyList<CategoryOption> categories, string problem);
}

/// <summary>Correcting a row of the preview that could not be imported as it was (no date, unreadable amount, negative amount...).</summary>
public sealed partial class ImportRowEditViewModel : ObservableObject
{
    private readonly ImportRow _row;
    private readonly IReadOnlyList<CategoryOption> _allCategories;
    private readonly CategoryTarget _byFile;

    public string Heading { get; }
    public string Problem { get; }
    public string Original { get; }
    public bool HasOriginal => Original.Length > 0;
    public string FileCategory { get; }
    public bool HasFileCategory => FileCategory.Length > 0;

    public ObservableCollection<CategoryTarget> Categories { get; } = [];

    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExpense))] private bool _isIncome;
    [ObservableProperty] private string _place;
    [ObservableProperty] private string _note;
    [ObservableProperty] private CategoryTarget? _selectedCategory;
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>What was typed, once Save or Skip has been chosen.</summary>
    public ImportRowFix? Result { get; private set; }

    public event Action<bool>? CloseRequested;

    public bool IsExpense
    {
        get => !IsIncome;
        set => IsIncome = !value;
    }

    public ImportRowEditViewModel(ImportRow row, IReadOnlyList<CategoryOption> categories, string problem)
    {
        _row = row;
        _allCategories = categories;
        Heading = Res.Format("ImportRowHeading", row.Sheet, row.ExcelRow);
        Problem = problem;
        Original = row.RawAmount.Length > 0 ? Res.Format("ImportRowOriginal", row.RawAmount) : "";
        FileCategory = row.SourceCategory.Length == 0 || row.IsFromSheetIncome ? "" : Res.Format("ImportRowFileCategory", row.SourceCategory);
        _byFile = new CategoryTarget(CategoryChoice.Uncategorized, Res.Get("ImportRowCategoryAsInFile"));

        _date = row.Date?.ToDateTime(TimeOnly.MinValue);
        // an amount that could not be read, or is in another currency, is not offered as if it were złoty
        var offerAmount = row.Amount > 0 && row.Message is not ("foreign-currency" or "bad-amount" or "income-cell");
        _amountText = offerAmount ? row.Amount.ToString("0.##", CultureInfo.CurrentCulture) : "";
        _isIncome = row.Type == TransactionType.Income;
        _place = row.Place;
        _note = row.Note;
        FillCategories();
        SelectedCategory = Categories.FirstOrDefault(c => c.Choice.ExistingId is { } id && id == row.CategoryId) ?? _byFile;
    }

    partial void OnIsIncomeChanged(bool value)
    {
        var keep = SelectedCategory?.Choice.ExistingId;
        FillCategories();
        SelectedCategory = Categories.FirstOrDefault(c => c.Choice.ExistingId is { } id && id == keep) ?? _byFile;
    }

    private void FillCategories()
    {
        var type = IsIncome ? TransactionType.Income : TransactionType.Expense;
        Categories.Clear();
        Categories.Add(_byFile);
        foreach (var o in _allCategories.Where(o => CategoryHelpers.Matches(o.Category.Type, type)))
            Categories.Add(new CategoryTarget(new CategoryChoice(o.Category.Id, null), o.DisplayName));
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = "";
        if (Date is null)
        {
            ErrorMessage = Res.Get("ImportRowNoDate");
            return;
        }
        if (!AmountExpression.TryEvaluate(AmountText, out var amount) || amount <= 0)
        {
            ErrorMessage = Res.Get("ImportRowBadAmount");
            return;
        }

        Result = new ImportRowFix(DateOnly.FromDateTime(Date.Value), Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            IsIncome ? TransactionType.Income : TransactionType.Expense, Place, Note, SelectedCategory?.Choice.ExistingId, Skip: false);
        CloseRequested?.Invoke(true);
    }

    /// <summary>Leaves the row out of the import.</summary>
    [RelayCommand]
    private void Skip()
    {
        Result = new ImportRowFix(_row.Date, _row.Amount, _row.Type, _row.Place, _row.Note, _row.CategoryId, Skip: true);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
