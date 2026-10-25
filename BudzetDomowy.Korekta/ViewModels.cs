using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.Core.Corrections;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.Korekta;

/// <summary>A transaction as the table shows it.</summary>
public sealed record TxRow(Transaction Model, string CategoryPath)
{
    public Guid Id => Model.Id;
    public DateTime Date => Model.Date.ToDateTime(TimeOnly.MinValue);
    public bool IsIncome => Model.Type == TransactionType.Income;
    public string TypeText => IsIncome ? "Przychód" : "Wydatek";
    public decimal AmountPln => Model.AmountPln;
    public string OwnAmountText => Model.CurrencyCode == "PLN" ? "" : $"{Model.Amount.ToString("N2", CultureInfo.CurrentCulture)} {Model.CurrencyCode}";
    public string Place => Model.Place;
    public string Note => Model.Note;
}

public sealed record CategoryItem(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record TypeItem(TransactionType? Type, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One transaction chosen for splitting, with the box for the amount of the new part.</summary>
public sealed partial class SplitRowViewModel : ObservableObject
{
    public SplitRowViewModel(TxRow row) => Row = row;

    public TxRow Row { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Part)), NotifyPropertyChangedFor(nameof(IsInvalid)), NotifyPropertyChangedFor(nameof(RestText))]
    private string _partText = "";

    /// <summary>The part that goes to the new entry, in the currency of the transaction; null when the box is empty or not understood.</summary>
    public decimal? Part => AmountExpression.TryEvaluate(PartText, out var v) && v > 0 ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;

    public bool IsEmpty => string.IsNullOrWhiteSpace(PartText);

    /// <summary>Typed, but not a number above zero and below the whole amount.</summary>
    public bool IsInvalid => !IsEmpty && (Part is not { } p || p >= Row.Model.Amount);

    public string RestText => Part is { } p && p < Row.Model.Amount ? (Row.Model.Amount - p).ToString("N2", CultureInfo.CurrentCulture) : "";

    public string AmountText => Row.Model.Amount.ToString("N2", CultureInfo.CurrentCulture) + (Row.Model.CurrencyCode == "PLN" ? "" : " " + Row.Model.CurrencyCode);
}

public sealed record PlanRowView(string KindText, string DateText, string Place, string BeforeText, string AfterText, string Category, string Note, bool IsAdd);

public sealed record DayRowView(string DateText, string IncomeBefore, string IncomeAfter, string IncomeChange, string IncomeExpected,
    string ExpenseBefore, string ExpenseAfter, string ExpenseChange, string ExpenseExpected, string CountBefore, string CountAfter, string State, bool Ok);

public sealed record TotalRowView(string Label, string Income, string Expense, string Count);

/// <summary>The result of the check after a correction (or after undoing it), ready to show.</summary>
public sealed class ReportView
{
    public ReportView(CorrectionRecord record, VerificationReport report, string caption)
    {
        Record = record;
        Report = report;
        Caption = caption;
        IsOk = report.AllOk;
        Banner = report.AllOk
            ? $"✔ Wszystko się zgadza. Sprawdzono dni z danymi: {report.DaysChecked}; zmiana dotyczy dni: {report.ChangedDays}."
            : $"✖ Niezgodności w dniach: {report.Problems.Count}. Zaznaczone na czerwono dni zmieniły się inaczej, niż zapowiadała korekta – rozważ cofnięcie.";
        Days = report.Days.Select(d => new DayRowView(d.Date.ToString("yyyy-MM-dd"), Money(d.Before.Income), Money(d.After.Income), Signed(d.Actual.Income), Signed(d.Expected.Income),
            Money(d.Before.Expense), Money(d.After.Expense), Signed(d.Actual.Expense), Signed(d.Expected.Expense), d.Before.Count.ToString(CultureInfo.CurrentCulture),
            d.After.Count.ToString(CultureInfo.CurrentCulture), d.Ok ? "OK" : "NIEZGODNE", d.Ok)).ToList();
        Totals =
        [
            new("Przed", Money(report.TotalBefore.Income), Money(report.TotalBefore.Expense), report.TotalBefore.Count.ToString(CultureInfo.CurrentCulture)),
            new("Po", Money(report.TotalAfter.Income), Money(report.TotalAfter.Expense), report.TotalAfter.Count.ToString(CultureInfo.CurrentCulture)),
            new("Zmiana rzeczywista", Signed(report.TotalAfter.Income - report.TotalBefore.Income), Signed(report.TotalAfter.Expense - report.TotalBefore.Expense),
                Signed(report.TotalAfter.Count - report.TotalBefore.Count)),
            new("Zmiana zapowiedziana", Signed(report.TotalExpected.Income), Signed(report.TotalExpected.Expense), Signed(report.TotalExpected.Count)),
        ];
    }

    public CorrectionRecord Record { get; }
    public VerificationReport Report { get; }
    public string Caption { get; }
    public bool IsOk { get; }
    public string Banner { get; }
    public IReadOnlyList<DayRowView> Days { get; }
    public IReadOnlyList<TotalRowView> Totals { get; }
    public bool CanRevert => !Record.Reverted;

    internal static string Money(decimal v) => v.ToString("N2", CultureInfo.CurrentCulture);

    internal static string Signed(decimal v) => v == 0 ? "0,00" : v.ToString("+#,##0.00;-#,##0.00", CultureInfo.CurrentCulture);

    internal static string Signed(int v) => v == 0 ? "0" : v.ToString("+0;-0", CultureInfo.CurrentCulture);
}

public sealed record HistoryItem(CorrectionRecord Record)
{
    public string Title => Record.Title;
    public string WhenText => Record.AppliedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string CountText => $"{Record.Changes.Count} wpisów";
    public string StateText => Record.Reverted ? "Cofnięta" : Record.AllOk ? "Kontrola OK" : "Kontrola: niezgodności";
}

/// <summary>The month-by-month check of the history of corrections, ready to show: what is the same, what differs and where.</summary>
public sealed partial class MonthReportView : ObservableObject
{
    private readonly IReadOnlyList<DayRowView> _changed;
    private readonly IReadOnlyList<DayRowView> _all;
    private const int ListedInText = 12;

    public MonthReportView(MonthlyAuditResult result, string caption)
    {
        Result = result;
        Caption = caption;
        var report = result.Report;
        IsOk = report.AllOk;

        var months = report.Differences;
        var unexpected = report.Problems;
        var inScope = $"Uwzględniono korekty: {result.CorrectionsIncluded}" + (result.UndosIncluded > 0 ? $", w tym cofnięcia: {result.UndosIncluded}" : "") + ".";

        if (months.Count == 0 && report.AllOk)
        {
            Headline = $"✔ W każdym z {report.DaysChecked} miesięcy sumy przychodów i wydatków są takie same przed korektami w ogóle i po korektach do wybranej. {inScope}";
            var entries = report.TotalChange.Count;
            Differences = entries == 0 ? "" : $"Zmieniła się tylko liczba wpisów: {ReportView.Signed(entries)} (co zapowiadały korekty).";
        }
        else if (report.AllOk)
        {
            Headline = $"Sumy różnią się w {months.Count} z {report.DaysChecked} miesięcy – tak, jak zapowiadały korekty. {inScope}";
            Differences = DifferencesText("Różnice w sumach", months, report.TotalChange, d => d.Actual);
        }
        else
        {
            Headline = $"✖ Niezgodności z zapowiedzią korekt w {unexpected.Count} z {report.DaysChecked} miesięcy. {inScope}";
            Differences = months.Count == 0 ? "Sumy żadnego miesiąca się nie zmieniły." : DifferencesText("Różnice w sumach", months, report.TotalChange, d => d.Actual);
        }
        Unexpected = unexpected.Count == 0
            ? ""
            : DifferencesText("Niezgodne z zapowiedzią (rzeczywista zmiana minus zapowiedziana)", unexpected, report.TotalUnexpected, d => d.Actual - d.Expected);

        _changed = report.Days.Select(Row).ToList();
        _all = report.All.Select(Row).ToList();
        Totals =
        [
            new("Bez żadnej korekty", ReportView.Money(report.TotalBefore.Income), ReportView.Money(report.TotalBefore.Expense), report.TotalBefore.Count.ToString(CultureInfo.CurrentCulture)),
            new("Po wybranych korektach", ReportView.Money(report.TotalAfter.Income), ReportView.Money(report.TotalAfter.Expense), report.TotalAfter.Count.ToString(CultureInfo.CurrentCulture)),
            new("Różnica", ReportView.Signed(report.TotalChange.Income), ReportView.Signed(report.TotalChange.Expense), ReportView.Signed(report.TotalChange.Count)),
            new("Zapowiedziana różnica", ReportView.Signed(report.TotalExpected.Income), ReportView.Signed(report.TotalExpected.Expense), ReportView.Signed(report.TotalExpected.Count)),
        ];
    }

    public MonthlyAuditResult Result { get; }
    public string Caption { get; }
    public bool IsOk { get; }
    public string Headline { get; }

    /// <summary>The total differences of the sums and the months they are in (empty when there are none).</summary>
    public string Differences { get; }

    /// <summary>The same for what differs from the promise (empty when everything is as promised).</summary>
    public string Unexpected { get; }

    public bool HasDifferences => Differences.Length > 0;
    public bool HasUnexpected => Unexpected.Length > 0;
    public IReadOnlyList<TotalRowView> Totals { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Rows))] private bool _showUnchanged;

    /// <summary>The months that changed or were promised to; with the box ticked, all months.</summary>
    public IReadOnlyList<DayRowView> Rows => ShowUnchanged ? _all : _changed;

    private static string Part(string name, decimal value) => value == 0 ? "" : $"{name} {ReportView.Signed(value)} zł";

    private static string Describe(DaySum sum)
    {
        var parts = new[] { Part("przychody", sum.Income), Part("wydatki", sum.Expense) }.Where(t => t.Length > 0).ToList();
        return parts.Count == 0 ? "bez zmiany sum" : string.Join(", ", parts);
    }

    /// <summary>"Różnice w sumach: przychody +5,00 zł, wydatki -20,00 zł. Miesiące: 2026-01 (wydatki -10,00 zł), 2026-02 (...)".</summary>
    private static string DifferencesText(string label, IReadOnlyList<DayCheck> months, DaySum total, Func<DayCheck, DaySum> change)
    {
        var listed = months.Take(ListedInText).Select(m => $"{m.Date:yyyy-MM} ({Describe(change(m))})");
        var more = months.Count > ListedInText ? $" … i jeszcze {months.Count - ListedInText} (wszystkie w tabeli i w Excelu)" : "";
        return $"{label}: {Describe(total)}. Miesiące ({months.Count}): {string.Join(", ", listed)}{more}.";
    }

    private static DayRowView Row(DayCheck d) => new(d.Date.ToString("yyyy-MM"), ReportView.Money(d.Before.Income), ReportView.Money(d.After.Income),
        ReportView.Signed(d.Actual.Income), ReportView.Signed(d.Expected.Income), ReportView.Money(d.Before.Expense), ReportView.Money(d.After.Expense),
        ReportView.Signed(d.Actual.Expense), ReportView.Signed(d.Expected.Expense), d.Before.Count.ToString(CultureInfo.CurrentCulture),
        d.After.Count.ToString(CultureInfo.CurrentCulture), d.Ok ? (d.Actual.IsEmpty && d.Expected.IsEmpty ? "bez zmian" : "OK") : "NIEZGODNE", d.Ok);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IUi _ui;
    private readonly string? _localRoot;
    private Session? _session;
    private List<TxRow> _all = [];
    private IReadOnlyList<TxRow> _selected = [];
    private Dictionary<Guid, Category> _categoriesById = [];

    public MainViewModel(IUi ui, string? localRoot = null)
    {
        _ui = ui;
        _localRoot = localRoot;
        TypeFilters = [new(null, "Wszystkie"), new(TransactionType.Expense, "Wydatki"), new(TransactionType.Income, "Przychody")];
        _typeFilter = TypeFilters[0];
        var year = DateTime.Today.Year;
        _addFrom = new DateTime(year, 1, 1);
        _addTo = new DateTime(year, 12, 31);
        _addIncome = false;
    }

    // ----- the data -----

    [ObservableProperty] private string _dataFolder = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanWork))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanWork))] private bool _isLoaded;
    [ObservableProperty] private string _status = "Wybierz folder z danymi programu.";
    [ObservableProperty] private int _selectedTab;

    public bool CanWork => IsLoaded && !IsBusy;

    public IReadOnlyList<TypeItem> TypeFilters { get; }

    public Session? CurrentSession => _session;

    /// <summary>The folder from the command line, the last one used, or the one the main program uses.</summary>
    public async Task StartAsync(string? argument)
    {
        var folder = argument ?? FolderMemory.Last() ?? FolderMemory.FromMainProgram();
        if (folder is not null && Directory.Exists(folder)) await LoadAsync(folder);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = _ui.PickFolder(string.IsNullOrWhiteSpace(DataFolder) ? null : DataFolder);
        if (folder is not null) await LoadAsync(folder);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!string.IsNullOrWhiteSpace(DataFolder)) await LoadAsync(DataFolder);
    }

    public async Task LoadAsync(string folder)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Wczytywanie danych… (przy pierwszym razie to chwilę trwa)";
        try
        {
            var old = _session;
            _session = null;
            old?.Dispose();
            var session = await Task.Run(() => Session.Open(folder, _localRoot));
            _session = session;
            DataFolder = folder;
            if (_localRoot is null) FolderMemory.Remember(folder);                 // tests work in a folder of their own and leave the real settings alone
            IsLoaded = true;
            ClearPlan();
            Report = null;
            LoadChoices();
            ReloadRows();
            RefreshHistory();
            Status = $"Wczytano wpisów: {_all.Count}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or UpdateRequiredException)
        {
            IsLoaded = false;
            Status = "Nie udało się wczytać danych: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadChoices()
    {
        var categories = _session!.Core.Reader.GetCategories();
        _categoriesById = categories.ToDictionary(c => c.Id);
        var options = CategoryHelpers.Flatten(categories, c => !c.IsArchived, alphabetical: true).Select(o => new CategoryItem(o.Category.Id, o.DisplayName)).ToList();
        FilterCategories = [new CategoryItem(null, "(wszystkie)"), .. options];
        ChoiceCategories = [new CategoryItem(null, "(wybierz kategorię)"), .. options];
        CategoryFilter = FilterCategories[0];
        SplitCategory = ChoiceCategories[0];
        AddCategory = ChoiceCategories[0];
    }

    private void ReloadRows()
    {
        var categories = _categoriesById;
        _all = _session!.Core.Reader.GetTransactions()
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt)
            .Select(t => new TxRow(t, CategoryHelpers.PathOf(t.CategoryId, categories))).ToList();
        ApplyFilters();
    }

    // ----- filters -----

    [ObservableProperty] private IReadOnlyList<CategoryItem> _filterCategories = [];
    [ObservableProperty] private IReadOnlyList<CategoryItem> _choiceCategories = [];
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private TypeItem _typeFilter;
    [ObservableProperty] private CategoryItem? _categoryFilter;
    [ObservableProperty] private string _placeText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _amountFromText = "";
    [ObservableProperty] private string _amountToText = "";
    [ObservableProperty] private IReadOnlyList<TxRow> _rows = [];
    [ObservableProperty] private string _shownText = "";

    partial void OnFromChanged(DateTime? value) => ApplyFilters();
    partial void OnToChanged(DateTime? value) => ApplyFilters();
    partial void OnTypeFilterChanged(TypeItem value) => ApplyFilters();
    partial void OnCategoryFilterChanged(CategoryItem? value) => ApplyFilters();
    partial void OnPlaceTextChanged(string value) => ApplyFilters();
    partial void OnNoteTextChanged(string value) => ApplyFilters();
    partial void OnAmountFromTextChanged(string value) => ApplyFilters();
    partial void OnAmountToTextChanged(string value) => ApplyFilters();

    [RelayCommand]
    private void ClearFilters()
    {
        From = null;
        To = null;
        TypeFilter = TypeFilters[0];
        CategoryFilter = FilterCategories.FirstOrDefault();
        PlaceText = "";
        NoteText = "";
        AmountFromText = "";
        AmountToText = "";
    }

    private static decimal? Bound(string text) => AmountParser.TryParse(text, out var v) ? v : null;

    private void ApplyFilters()
    {
        if (_session is null) return;
        IEnumerable<TxRow> query = _all;
        if (From is { } from) query = query.Where(r => r.Date >= from.Date);
        if (To is { } to) query = query.Where(r => r.Date <= to.Date);
        if (TypeFilter.Type is { } type) query = query.Where(r => r.Model.Type == type);
        if (CategoryFilter?.Id is { } category)
        {
            var ids = CategoryHelpers.WithDescendants(category, _categoriesById.Values);
            query = query.Where(r => r.Model.CategoryId is { } c && ids.Contains(c));
        }
        if (PlaceText.Trim() is { Length: > 0 } place) query = query.Where(r => r.Place.Contains(place, StringComparison.CurrentCultureIgnoreCase));
        if (NoteText.Trim() is { Length: > 0 } note) query = query.Where(r => r.Note.Contains(note, StringComparison.CurrentCultureIgnoreCase));
        var low = Bound(AmountFromText);
        var high = Bound(AmountToText);
        if (low is { } lo && high is { } hi && lo > hi) (low, high) = (hi, lo);
        if (low is { } min) query = query.Where(r => r.AmountPln >= min);
        if (high is { } max) query = query.Where(r => r.AmountPln <= max);

        Rows = query.ToList();
        ShownText = $"Pokazano wpisów: {Rows.Count} z {_all.Count}. {Totals(Rows)}";
        SetSelection([]);
    }

    private static string Totals(IEnumerable<TxRow> rows)
    {
        var list = rows.ToList();
        var expense = list.Where(r => !r.IsIncome).Sum(r => r.AmountPln);
        var income = list.Where(r => r.IsIncome).Sum(r => r.AmountPln);
        return $"Wydatki: {expense.ToString("N2", CultureInfo.CurrentCulture)} zł, przychody: {income.ToString("N2", CultureInfo.CurrentCulture)} zł.";
    }

    // ----- the selection -----

    [ObservableProperty] private string _selectionText = "Nic nie zaznaczono.";

    public IReadOnlyList<TxRow> Selected => _selected;

    public void SetSelection(IEnumerable<TxRow> selected)
    {
        _selected = selected.ToList();
        SelectionText = _selected.Count == 0 ? "Nic nie zaznaczono." : $"Zaznaczono wpisów: {_selected.Count}. {Totals(_selected)}";
        SplitItems = new ObservableCollection<SplitRowViewModel>(_selected.Select(r => new SplitRowViewModel(r)));
        ClearPlan();
    }

    // ----- changing the amounts -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShiftIncrease))] private bool _shiftDecrease = true;

    /// <summary>The other half of the pair of radio buttons.</summary>
    public bool ShiftIncrease
    {
        get => !ShiftDecrease;
        set => ShiftDecrease = !value;
    }
    [ObservableProperty] private string _shiftAmountText = "";

    [RelayCommand]
    private void PlanShift()
    {
        if (!AmountParser.TryParse(ShiftAmountText, out var amount) || amount <= 0)
        {
            Status = "Podaj kwotę większą od zera, o którą mają się zmienić wpisy.";
            return;
        }
        if (_selected.Count == 0)
        {
            Status = "Zaznacz wpisy w tabeli (Ctrl+A zaznacza wszystkie widoczne).";
            return;
        }

        var delta = ShiftDecrease ? -amount : amount;
        var title = (ShiftDecrease ? "Zmniejszenie" : "Zwiększenie") + $" kwot o {amount.ToString("N2", CultureInfo.CurrentCulture)} zł";
        SetPlan(CorrectionPlanner.ShiftAmounts(_selected.Select(r => r.Model), delta, title));
    }

    // ----- splitting -----

    [ObservableProperty] private ObservableCollection<SplitRowViewModel> _splitItems = [];
    [ObservableProperty] private CategoryItem? _splitCategory;
    [ObservableProperty] private string _splitAllText = "";

    [RelayCommand]
    private void SplitFillAll()
    {
        foreach (var item in SplitItems) item.PartText = SplitAllText;
    }

    [RelayCommand]
    private void SplitHalf()
    {
        foreach (var item in SplitItems) item.PartText = (Math.Round(item.Row.Model.Amount / 2m, 2, MidpointRounding.AwayFromZero)).ToString("0.##", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void PlanSplit()
    {
        if (_session is null) return;
        if (SplitCategory?.Id is not { } categoryId)
        {
            Status = "Wybierz kategorię dla rozbitych części.";
            return;
        }
        var typed = SplitItems.Where(i => !i.IsEmpty).ToList();
        if (typed.Count == 0)
        {
            Status = "Wpisz kwoty części do wydzielenia (w tabeli rozbijania).";
            return;
        }
        if (typed.Any(i => i.IsInvalid))
        {
            Status = "Niektóre kwoty są niepoprawne (zaznaczone na czerwono): część musi być większa od zera i mniejsza od kwoty wpisu.";
            return;
        }

        // an income cannot be given an expense category and the other way round: such entries are left out and reported
        var category = _categoriesById[categoryId];
        var requests = typed.Where(i => CategoryHelpers.Matches(category.Type, i.Row.Model.Type)).Select(i => new SplitRequest(i.Row.Model, i.Part!.Value)).ToList();
        var mismatched = typed.Count - requests.Count;
        var plan = CorrectionPlanner.Split(requests, categoryId, _session.Core.Device.DeviceId, DateTimeOffset.Now, $"Rozbicie wpisów: {requests.Count}, kategoria „{SplitCategory.Label}”");
        SetPlan(plan, mismatched > 0 ? $"Pominięte, bo kategoria nie pasuje do rodzaju wpisu: {mismatched}." : "");
    }

    // ----- adding every month -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddExpense))] private bool _addIncome;

    public bool AddExpense
    {
        get => !AddIncome;
        set => AddIncome = !value;
    }
    [ObservableProperty] private string _addAmountText = "";
    [ObservableProperty] private CategoryItem? _addCategory;
    [ObservableProperty] private string _addPlace = "";
    [ObservableProperty] private string _addNote = "";
    [ObservableProperty] private DateTime? _addFrom;
    [ObservableProperty] private DateTime? _addTo;
    [ObservableProperty] private int _addDay = 1;
    [ObservableProperty] private string _addDatesText = "";

    partial void OnAddFromChanged(DateTime? value) => UpdateAddDates();
    partial void OnAddToChanged(DateTime? value) => UpdateAddDates();
    partial void OnAddDayChanged(int value) => UpdateAddDates();

    private IReadOnlyList<DateOnly> AddDates() =>
        AddFrom is { } f && AddTo is { } t ? CorrectionPlanner.MonthlyDates(DateOnly.FromDateTime(f), DateOnly.FromDateTime(t), AddDay) : [];

    private void UpdateAddDates()
    {
        var dates = AddDates();
        AddDatesText = dates.Count == 0
            ? "Brak dat: sprawdź zakres i dzień miesiąca."
            : $"Powstanie wpisów: {dates.Count} (od {dates[0]:yyyy-MM-dd} do {dates[^1]:yyyy-MM-dd}).";
    }

    [RelayCommand]
    private void PlanAdd()
    {
        if (_session is null) return;
        if (!AmountParser.TryParse(AddAmountText, out var amount) || amount <= 0)
        {
            Status = "Podaj kwotę większą od zera.";
            return;
        }
        var dates = AddDates();
        if (dates.Count == 0 || AddFrom is not { } from || AddTo is not { } to)
        {
            Status = "Brak dat do dodania: sprawdź zakres i dzień miesiąca.";
            return;
        }
        if (dates.Count > CorrectionPlanner.MaxAdded)
        {
            Status = $"Naraz można dodać najwyżej {CorrectionPlanner.MaxAdded} wpisów.";
            return;
        }

        var type = AddIncome ? TransactionType.Income : TransactionType.Expense;
        var categoryId = AddCategory?.Id;
        if (categoryId is { } id && !CategoryHelpers.Matches(_categoriesById[id].Type, type))
        {
            Status = "Kategoria nie pasuje do rodzaju wpisu (przychód / wydatek).";
            return;
        }

        var spec = new MonthlyAddSpec(type, amount, categoryId, AddPlace, AddNote, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), AddDay);
        SetPlan(CorrectionPlanner.AddMonthly(spec, _session.Core.Device.DeviceId, DateTimeOffset.Now,
            $"Dodanie co miesiąc: {(AddIncome ? "przychód" : "wydatek")} {amount.ToString("N2", CultureInfo.CurrentCulture)} zł ({dates.Count} wpisów)"));
    }

    // ----- the plan -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApply))] private CorrectionPlan? _plan;
    [ObservableProperty] private IReadOnlyList<PlanRowView> _planRows = [];
    [ObservableProperty] private string _planSummary = "";
    [ObservableProperty] private string _planSkipped = "";

    public bool CanApply => Plan is { IsEmpty: false } && CanWork;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    private void ClearPlan()
    {
        Plan = null;
        PlanRows = [];
        PlanSummary = "";
        PlanSkipped = "";
    }

    private void SetPlan(CorrectionPlan plan, string extraSkipped = "")
    {
        Plan = plan;
        PlanRows = plan.Changes.OrderBy(c => c.After.Date).ThenBy(c => c.After.Place).Select(c => new PlanRowView(
            c.Kind == ChangeKind.Add ? "Nowy" : "Zmiana", c.After.Date.ToString("yyyy-MM-dd"), c.After.Place,
            c.Before is null ? "" : AmountText(c.Before), AmountText(c.After), _categoriesById.TryGetValue(c.After.CategoryId ?? Guid.Empty, out var cat) ? cat.Name : "",
            c.After.Note, c.Kind == ChangeKind.Add)).ToList();

        var deltas = plan.ExpectedDeltas().Values.Aggregate(DaySum.Empty, (a, b) => a + b);
        PlanSummary = plan.IsEmpty
            ? "Ta korekta niczego nie zmieni."
            : $"{plan.Title}. Do zmiany: {plan.Updates}, nowych: {plan.Adds}. Skutek dla sum: przychody {ReportView.Signed(deltas.Income)} zł, wydatki {ReportView.Signed(deltas.Expense)} zł, wpisów {ReportView.Signed(deltas.Count)}.";

        var reasons = plan.Skipped.GroupBy(s => s.Reason).Select(g => $"{ReasonText(g.Key)}: {g.Count()}").ToList();
        PlanSkipped = string.Join(" ", new[] { reasons.Count == 0 ? "" : "Pominięte – " + string.Join(", ", reasons) + ".", extraSkipped }.Where(s => s.Length > 0));
        Status = plan.IsEmpty ? "Ta korekta niczego nie zmieni." : "Zapowiedź gotowa: sprawdź tabelę na dole i kliknij „Zastosuj i sprawdź”.";
    }

    private static string AmountText(Transaction t) =>
        t.Amount.ToString("N2", CultureInfo.CurrentCulture) + (t.CurrencyCode == "PLN" ? "" : " " + t.CurrencyCode);

    private static string ReasonText(SkipReason reason) => reason switch
    {
        SkipReason.NotPositive => "kwota po zmianie nie byłaby większa od zera",
        SkipReason.PartTooBig => "część nie jest mniejsza od kwoty wpisu",
        SkipReason.NoPart => "brak kwoty części",
        _ => "wpis z kilkoma pozycjami",
    };

    [RelayCommand]
    private void DiscardPlan()
    {
        ClearPlan();
        Status = "Zapowiedź odrzucona.";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Plan is not { IsEmpty: false } plan || _session is null || IsBusy) return;
        var text = $"{PlanSummary}\n\nPrzed zapisem program zrobi kopię zapasową danych. Zmiany trafią do danych i na pozostałe komputery; potem zostaną automatycznie sprawdzone dzień po dniu, a korektę będzie można cofnąć.";
        if (!_ui.Confirm("Zastosować korektę?", text)) return;

        var session = _session;
        IsBusy = true;
        Status = "Zapisywanie korekty i sprawdzanie…";
        try
        {
            var result = await Task.Run(() => session.Runner.Apply(plan));
            ShowReport(result.Record, result.Report, "Kontrola po korekcie");
            ClearPlan();
            ReloadRows();
            RefreshHistory();
            SelectedTab = 1;
            Status = result.Report.AllOk ? "Korekta zapisana, kontrola zgodna." : "Korekta zapisana, ale kontrola znalazła niezgodności.";
        }
        catch (CorrectionConflictException e)
        {
            Status = "Nic nie zapisano: " + e.Message + " Dane zmieniły się od zapowiedzi – wczytaj je ponownie i zaplanuj korektę jeszcze raz.";
            ClearPlan();
            ReloadRows();
        }
        catch (Exception e) when (e is UpdateRequiredException or IOException or UnauthorizedAccessException)
        {
            Status = "Nie udało się zapisać: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ----- the check, the history, the undo -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReport))] private ReportView? _report;
    [ObservableProperty] private IReadOnlyList<HistoryItem> _history = [];
    [ObservableProperty] private HistoryItem? _selectedHistory;

    public bool HasReport => Report is not null;

    private void ShowReport(CorrectionRecord record, VerificationReport report, string caption) => Report = new ReportView(record, report, caption);

    private void RefreshHistory()
    {
        History = _session is null ? [] : _session.Runner.History().Select(r => new HistoryItem(r)).ToList();
        SelectedHistory = null;
    }

    [RelayCommand]
    private void ShowSelectedHistory()
    {
        if (SelectedHistory is not { } item) return;
        ShowReport(item.Record, CorrectionRunner.ReportOf(item.Record), $"Kontrola zapisana przy korekcie z {item.WhenText}");
        SelectedTab = 1;
    }

    [RelayCommand]
    private async Task RevertAsync(HistoryItem? item)
    {
        var record = item?.Record ?? Report?.Record;
        if (record is null || _session is null || IsBusy) return;
        if (record.Reverted)
        {
            Status = "Ta korekta została już cofnięta.";
            return;
        }
        if (!_ui.Confirm("Cofnąć korektę?", $"„{record.Title}”\nPrzywrócone zostaną poprzednie wersje zmienionych wpisów, a dodane wpisy znikną. Cofnięcie też zostanie sprawdzone dzień po dniu."))
            return;

        var session = _session;
        IsBusy = true;
        Status = "Cofanie korekty…";
        try
        {
            var report = await Task.Run(() => session.Runner.Revert(record));
            ShowReport(record, report, "Kontrola po cofnięciu");
            ReloadRows();
            RefreshHistory();
            SelectedTab = 1;
            Status = report.AllOk ? "Korekta cofnięta, kontrola zgodna." : "Korekta cofnięta, ale kontrola znalazła niezgodności.";
        }
        catch (Exception e) when (e is UpdateRequiredException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Nie udało się cofnąć: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ----- the month-by-month check of the history -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMonthReport))] private MonthReportView? _monthReport;

    public bool HasMonthReport => MonthReport is not null;

    [RelayCommand]
    private async Task AuditSelectedAsync()
    {
        if (SelectedHistory is not { } item)
        {
            Status = "Zaznacz na liście korektę, do której mam sprawdzić miesiące.";
            return;
        }
        await RunAuditAsync(history => HistoryAudit.Run(_session!.DataFolder, history, item.Record), $"Stan po korekcie „{item.Title}” z {item.WhenText}");
    }

    [RelayCommand]
    private async Task AuditAllAsync() =>
        await RunAuditAsync(history => HistoryAudit.RunToTheEnd(_session!.DataFolder, history), "Stan po wszystkich korektach (z cofnięciami)");

    private async Task RunAuditAsync(Func<IReadOnlyList<CorrectionRecord>, MonthlyAuditResult> run, string caption)
    {
        if (_session is null || IsBusy) return;
        var session = _session;
        IsBusy = true;
        Status = "Odtwarzanie danych z dzienników i porównywanie miesięcy…";
        try
        {
            var result = await Task.Run(() => run(session.Runner.History()));
            MonthReport = new MonthReportView(result, caption);
            Status = result.Report.AllOk ? "Kontrola miesięcy zakończona: zgodnie z zapowiedzią." : "Kontrola miesięcy znalazła niezgodności.";
        }
        catch (AuditNotSupportedException e)
        {
            Status = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Nie udało się sprawdzić miesięcy: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportMonthsAsync()
    {
        if (MonthReport is not { } report) return;
        var path = _ui.PickSaveFile($"Kontrola miesięcy {DateTime.Now:yyyy-MM-dd HHmm}.xlsx");
        if (path is null) return;
        try
        {
            await Task.Run(() => VerificationExport.Write(path, $"Kontrola miesięcy – {report.Caption}", report.Result.Report, monthly: true));
            Status = "Raport zapisany: " + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "Nie udało się zapisać raportu: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Report is not { } report) return;
        var path = _ui.PickSaveFile($"Kontrola korekty {report.Record.AppliedAt.ToLocalTime():yyyy-MM-dd HHmm}.xlsx");
        if (path is null) return;
        try
        {
            await Task.Run(() => VerificationExport.Write(path, $"{report.Record.Title} – {report.Caption}", report.Report));
            Status = "Raport zapisany: " + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "Nie udało się zapisać raportu: " + e.Message;
        }
    }
}
