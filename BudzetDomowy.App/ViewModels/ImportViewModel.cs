using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed partial class SheetItem : ObservableObject
{
    public SheetData Data { get; }
    public SheetAnalysis Analysis { get; }
    public string Name => Data.Name;
    public bool CanImport => Analysis.LooksLikeData;
    public string Info { get; }

    [ObservableProperty] private bool _isSelected;

    public SheetItem(SheetData data, SheetAnalysis analysis)
    {
        Data = data;
        Analysis = analysis;
        Info = analysis.LooksLikeData
            ? Res.Format("ImportSheetRows", analysis.DataRows)
            : Res.Get("ImportSheetNo_" + (analysis.Reason is "no-date" or "no-rows" or "empty" or "no-header" ? analysis.Reason : "no-header"));
        _isSelected = analysis.LooksLikeData;
    }
}

public sealed record ColumnChoice(int? Index, string Label)
{
    public override string ToString() => Label;
}

public sealed record CategoryTarget(CategoryChoice Choice, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class CategoryMapRow : ObservableObject
{
    public string Key { get; }
    public string SourceName { get; }
    public string CountText { get; }
    public IReadOnlyList<CategoryTarget> Targets { get; }

    [ObservableProperty] private CategoryTarget _selected;

    public CategoryMapRow(string key, string sourceName, int count, IReadOnlyList<CategoryTarget> targets, CategoryTarget selected)
    {
        Key = key;
        SourceName = sourceName == ImportParser.SheetIncomeCategory ? Res.Get("ImportSheetIncome") : sourceName;
        CountText = Res.Format("ImportRowsCount", count);
        Targets = targets;
        _selected = selected;
    }
}

/// <summary>A row of the preview; <see cref="Index"/> is its place in the plan, <see cref="CanFix"/> says whether it can be corrected.</summary>
public sealed record PlanRowView(string Sheet, int ExcelRow, string Date, string Amount, bool IsIncome, string Place, string Note, string Category,
    string Status, string Kind, int Index = -1, bool CanFix = false);

public sealed partial class ImportViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly IFileDialogs _dialogs;
    private readonly ImportMappingStore _store;
    private readonly IImportRowEditor? _editor;
    private IReadOnlyList<SheetData> _sheets = [];
    private ImportPlan? _plan;
    private bool _manualMapping;
    private bool _applyingMap;

    public ObservableCollection<SheetItem> Sheets { get; } = [];
    public ObservableCollection<CategoryMapRow> CategoryRows { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStep1)), NotifyPropertyChangedFor(nameof(IsStep2)), NotifyPropertyChangedFor(nameof(IsStep3)),
        NotifyPropertyChangedFor(nameof(IsStep4)), NotifyPropertyChangedFor(nameof(CanGoBack)), NotifyPropertyChangedFor(nameof(CanGoNext)), NotifyPropertyChangedFor(nameof(IsDone))]
    private int _step = 1;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoNext)), NotifyPropertyChangedFor(nameof(CanGoBack))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoNext))] private string _filePath = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _negativeIsExpense;
    [ObservableProperty] private bool _useIncomeLabel;
    [ObservableProperty] private string _incomeLabel = "Przychody";
    [ObservableProperty] private bool _incomeLabelFound;
    [ObservableProperty] private string _incomeCell = "";
    [ObservableProperty] private string _incomeCellHint = "";

    // columns
    [ObservableProperty] private IReadOnlyList<ColumnChoice> _columnChoices = [];
    [ObservableProperty] private ColumnChoice? _dateColumn;
    [ObservableProperty] private ColumnChoice? _placeColumn;
    [ObservableProperty] private ColumnChoice? _categoryColumn;
    [ObservableProperty] private ColumnChoice? _noteColumn;
    [ObservableProperty] private ColumnChoice? _amountColumn;
    [ObservableProperty] private ColumnChoice? _typeColumn;
    [ObservableProperty] private int _headerRowNumber = 1;
    [ObservableProperty] private string _mappingSheetName = "";

    // preview
    [ObservableProperty] private IReadOnlyList<PlanRowView> _previewRows = [];

    // the preview can be narrowed with check boxes: nothing checked shows every row; the checked kinds of rows (any of them), and then the checked types
    [ObservableProperty] private bool _onlyToImport;
    [ObservableProperty] private bool _onlyWarnings;
    [ObservableProperty] private bool _onlyDuplicates;
    [ObservableProperty] private bool _onlyErrors;
    [ObservableProperty] private bool _onlySkipped;
    [ObservableProperty] private bool _onlyEdited;
    [ObservableProperty] private bool _onlyIncome;
    [ObservableProperty] private bool _onlyExpense;
    [ObservableProperty] private string _shownText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(FixRowCommand))] private PlanRowView? _selectedPreviewRow;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _progressMax = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsDone => Step == 5;
    public bool CanGoBack => !IsBusy && Step is > 1 and < 5;
    public bool CanGoNext => !IsBusy && Step switch { 1 => FilePath.Length > 0 && Sheets.Any(s => s.IsSelected && s.CanImport), < 4 => true, _ => false };

    public event Action? CloseRequested;

    public ImportViewModel(BudgetCore core, IFileDialogs dialogs, ImportMappingStore store, IImportRowEditor? editor = null)
    {
        _core = core;
        _dialogs = dialogs;
        _store = store;
        _editor = editor;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(OnlyToImport) or nameof(OnlyWarnings) or nameof(OnlyDuplicates) or nameof(OnlyErrors) or nameof(OnlySkipped)
            or nameof(OnlyEdited) or nameof(OnlyIncome) or nameof(OnlyExpense))
            ShowPreview();
    }

    public bool HasPreviewFilter => OnlyToImport || OnlyWarnings || OnlyDuplicates || OnlyErrors || OnlySkipped || OnlyEdited || OnlyIncome || OnlyExpense;

    /// <summary>Whether a row passes the check boxes.</summary>
    private bool PassesFilter(ImportRow r)
    {
        var anyKind = OnlyToImport || OnlyWarnings || OnlyDuplicates || OnlyErrors || OnlySkipped || OnlyEdited;
        if (anyKind && !((OnlyToImport && r.WillBeImported) || (OnlyWarnings && r.Status == ImportRowStatus.Warning) || (OnlyDuplicates && r.Status == ImportRowStatus.Duplicate)
                         || (OnlyErrors && r.Status == ImportRowStatus.Error) || (OnlySkipped && r.Status == ImportRowStatus.Skipped) || (OnlyEdited && r.IsEdited)))
            return false;
        if (OnlyIncome != OnlyExpense) return OnlyIncome ? r.Type == TransactionType.Income : r.Type == TransactionType.Expense;
        return true;
    }

    [RelayCommand]
    private void ClearPreviewFilter() =>
        (OnlyToImport, OnlyWarnings, OnlyDuplicates, OnlyErrors, OnlySkipped, OnlyEdited, OnlyIncome, OnlyExpense) = (false, false, false, false, false, false, false, false);

    partial void OnIncomeCellChanged(string value) => UpdateIncomeCellHint();
    partial void OnIncomeLabelFoundChanged(bool value) => UpdateIncomeCellHint();

    /// <summary>Tells what the address points at in the first chosen sheet, so that a wrong cell is noticed before the import.</summary>
    private void UpdateIncomeCellHint()
    {
        var text = IncomeCell.Trim();
        if (text.Length == 0)
        {
            IncomeCellHint = Sheets.Count > 0 && !IncomeLabelFound ? Res.Format("ImportIncomeCellNone", IncomeLabel) : "";
            return;
        }
        if (!SheetData.TryParseAddress(text, out var row, out var column))
        {
            IncomeCellHint = Res.Get("ImportIncomeCellBadAddress");
            return;
        }

        var sheet = (Selected.FirstOrDefault() ?? Sheets.FirstOrDefault())?.Data;
        if (sheet is null)
        {
            IncomeCellHint = "";
            return;
        }
        var address = SheetData.Address(row, column);
        var value = sheet.Text(row, column);
        IncomeCellHint = value.Length == 0
            ? Res.Format("ImportIncomeCellEmpty", sheet.Name, address)
            : Res.Format("ImportIncomeCellValue", sheet.Name, address, value);
    }

    partial void OnDateColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnPlaceColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnCategoryColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnNoteColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnAmountColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnTypeColumnChanged(ColumnChoice? value) => MappingEdited();
    partial void OnHeaderRowNumberChanged(int value) => MappingEdited();

    private void MappingEdited()
    {
        if (!_applyingMap) _manualMapping = true;
    }

    // ----- step 1: file -----

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = _dialogs.OpenFile(Res.Get("ImportOpenTitle"), Res.Get("ImportOpenFilter"));
        if (path is null) return;
        FilePath = path;
        await LoadFileAsync();
    }

    public async Task LoadFileAsync()
    {
        Message = "";
        IsBusy = true;
        try
        {
            _sheets = await Task.Run(() => WorkbookLoader.Load(FilePath));
            Sheets.Clear();
            foreach (var s in _sheets) Sheets.Add(new SheetItem(s, ImportAnalyzer.Analyze(s)));
            foreach (var item in Sheets) item.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CanGoNext));
                UpdateIncomeCellHint();
            };

            IncomeLabelFound = _sheets.Any(s => ImportParser.ReadLabelledIncome(s, IncomeLabel, ImportAnalyzer.FindSheetMonth(s)) is not null);
            UseIncomeLabel = IncomeLabelFound;
            UpdateIncomeCellHint();
            _manualMapping = false;
            if (!Sheets.Any(s => s.CanImport)) Message = Res.Get("ImportNothingFound");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException
                                      or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or NotSupportedException or ArgumentException or FormatException)
        {
            Sheets.Clear();
            Message = Res.Format("ImportOpenFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    // ----- navigation -----

    [RelayCommand]
    private async Task NextAsync()
    {
        Message = "";
        if (!CanGoNext) return;
        switch (Step)
        {
            case 1:
                PrepareColumns();
                Step = 2;
                break;
            case 2:
                if (!MappingIsUsable()) { Message = Res.Get("ImportNeedAmountAndDate"); return; }
                await BuildPlanAsync();
                if (_plan is not null) Step = 3;
                break;
            case 3:
                ShowPreview();
                Step = 4;
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Message = "";
        if (Step > 1 && Step < 5) Step--;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    // ----- step 2: columns -----

    private IEnumerable<SheetItem> Selected => Sheets.Where(s => s.IsSelected && s.CanImport);

    private void PrepareColumns()
    {
        var first = Selected.First();
        var map = first.Analysis.Map!;
        MappingSheetName = first.Name;

        var sheet = first.Data;
        var choices = new List<ColumnChoice> { new(null, Res.Get("ImportNoColumn")) };
        for (var c = 0; c < Math.Min(sheet.ColumnCount, 30); c++)
        {
            var heading = sheet.Text(map.HeaderRow, c);
            var sample = heading.Length > 0 ? heading : FirstTextBelow(sheet, map.FirstDataRow, c);
            choices.Add(new ColumnChoice(c, sample.Length > 0 ? $"{SheetData.ColumnLetter(c)} – {Trim(sample)}" : SheetData.ColumnLetter(c)));
        }
        ColumnChoices = choices;

        _applyingMap = true;
        DateColumn = Pick(map.Date);
        PlaceColumn = Pick(map.Place);
        CategoryColumn = Pick(map.Category);
        NoteColumn = Pick(map.Note);
        AmountColumn = Pick(map.Amount);
        TypeColumn = Pick(map.Type);
        HeaderRowNumber = map.HeaderRow + 1;
        _applyingMap = false;
        _manualMapping = false;

        ColumnChoice Pick(int? index) => choices.First(c => c.Index == index);
    }

    private static string FirstTextBelow(SheetData sheet, int fromRow, int column)
    {
        for (var r = fromRow; r < Math.Min(sheet.RowCount, fromRow + 40); r++)
            if (sheet.At(r, column) is { } v && SheetData.TextOf(v) is { Length: > 0 } t) return t;
        return "";
    }

    private static string Trim(string text) => text.Length > 28 ? text[..28] + "…" : text;

    private bool MappingIsUsable() => !_manualMapping || (AmountColumn?.Index is not null && DateColumn?.Index is not null);

    private ColumnMap ManualMap() => new(Math.Max(0, HeaderRowNumber - 1), DateColumn?.Index, PlaceColumn?.Index, CategoryColumn?.Index,
        NoteColumn?.Index, AmountColumn?.Index, TypeColumn?.Index);

    // ----- step 3: plan and categories -----

    private async Task BuildPlanAsync()
    {
        IsBusy = true;
        try
        {
            var selections = Selected.Select(s => new SheetSelection(s.Data, _manualMapping ? ManualMap() : s.Analysis.Map!, s.Analysis.SheetMonth)).ToList();
            var options = new ImportOptions
            {
                Sign = NegativeIsExpense ? SignMode.NegativeIsExpense : SignMode.AllExpenses,
                IncomeLabel = UseIncomeLabel && IncomeLabel.Trim().Length > 0 ? IncomeLabel.Trim() : null,
                IncomeCell = IncomeCell.Trim().Length > 0 ? IncomeCell.Trim() : null,
            };
            var plan = await Task.Run(() => _core.Import.Plan(selections, options));
            if (plan.Rows.Count == 0)
            {
                Message = Res.Get("ImportNoRows");
                return;
            }
            _plan = plan;
            BuildCategoryRows(plan);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildCategoryRows(ImportPlan plan)
    {
        var suggested = _core.Import.SuggestMapping(plan, _store.Load());
        var categories = _core.Reader.GetCategories();
        var all = CategoryHelpers.Flatten(categories, c => !c.IsArchived, alphabetical: true);

        CategoryRows.Clear();
        foreach (var (name, count) in plan.SourceCategories)
        {
            var key = TextNorm.Normalize(name);
            var targets = new List<CategoryTarget> { new(CategoryChoice.Uncategorized, Res.Get("ImportUncategorized")) };
            var display = name == ImportParser.SheetIncomeCategory ? Res.Get("ImportSheetIncome") : name.Trim();
            targets.Add(new CategoryTarget(new CategoryChoice(null, name == ImportParser.SheetIncomeCategory ? "Inne przychody" : name.Trim()),
                Res.Format("ImportCreateNew", name == ImportParser.SheetIncomeCategory ? "Inne przychody" : display)));
            targets.AddRange(all.Select(o => new CategoryTarget(new CategoryChoice(o.Id, null), o.DisplayName)));

            var choice = suggested[key];
            var selected = targets.FirstOrDefault(t => t.Choice == choice)
                           ?? targets.FirstOrDefault(t => t.Choice.ExistingId is { } id && id == choice.ExistingId)
                           ?? targets.FirstOrDefault(t => t.Choice.NewName is { } n && n == choice.NewName)
                           ?? targets[0];
            CategoryRows.Add(new CategoryMapRow(key, name, count, targets, selected));
        }
    }

    // ----- step 4: preview and import -----

    private void ShowPreview()
    {
        if (_plan is null) return;
        var plan = _plan;
        Summary = Res.Format("ImportSummary", plan.ToImport, plan.Duplicates, plan.Warnings, plan.Errors, plan.Skipped);

        var matching = plan.Rows.Select((r, i) => (Row: r, Index: i)).Where(x => PassesFilter(x.Row)).ToList();
        ShownText = HasPreviewFilter ? Res.Format("ImportShown", matching.Count, plan.Rows.Count) : "";
        OnPropertyChanged(nameof(HasPreviewFilter));
        var rows = matching;

        var categories = _core.Reader.GetCategories().ToDictionary(c => c.Id);
        PreviewRows = rows.Take(3000).Select(x =>
        {
            var r = x.Row;
            var category = r.CategoryId is { } id && categories.ContainsKey(id) ? CategoryHelpers.PathOf(id, categories)
                : r.SourceCategory == ImportParser.SheetIncomeCategory ? Res.Get("ImportSheetIncome") : r.SourceCategory;
            return new PlanRowView(
                r.Sheet, r.ExcelRow, r.Date?.ToString("d", CultureInfo.CurrentCulture) ?? "—",
                r.Amount == 0 ? "" : (r.Type == TransactionType.Income ? "+" : "−") + r.Amount.ToString("N2", CultureInfo.CurrentCulture),
                r.Type == TransactionType.Income, r.Place, r.Note, category, StatusText(r), r.Status.ToString(), x.Index, IsFixable(r));
        }).ToList();
        SelectedPreviewRow = null;
    }

    /// <summary>Rows the user can correct: the ones that could not be read, the ones without a category, the skipped ones (a sum row, a zero, a missing amount, or left out by the user) and the ones already corrected.</summary>
    private static bool IsFixable(ImportRow r) => r.Status is ImportRowStatus.Error or ImportRowStatus.Warning or ImportRowStatus.Skipped || r.IsEdited;

    private static string StatusText(ImportRow r) => r.Status switch
    {
        ImportRowStatus.New when r.IsEdited => Res.Get("ImportStatusEdited"),
        ImportRowStatus.New => Res.Get("ImportStatusNew"),
        _ => Res.Get("ImportMsg_" + r.Message) is var m && !m.StartsWith('!')
            ? (r.IsEdited && r.Status != ImportRowStatus.Skipped ? Res.Get("ImportStatusEdited") + "; " + m : m)
            : r.Message,
    };

    // ----- correcting a row of the preview -----

    private bool CanFixRow() => SelectedPreviewRow is { CanFix: true } && _editor is not null;

    /// <summary>Opens the window to correct the selected row (also on a double click); the plan is checked again afterwards.</summary>
    [RelayCommand(CanExecute = nameof(CanFixRow))]
    private void FixRow()
    {
        if (_plan is not { } plan || SelectedPreviewRow is not { CanFix: true } view || _editor is null) return;
        var row = plan.Rows[view.Index];
        var categories = CategoryHelpers.Flatten(_core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
        var problem = StatusText(row);
        if (_editor.Edit(row, categories, problem) is not { } fix) return;

        _plan = _core.Import.Fix(plan, view.Index, fix);
        ShowPreview();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_plan is not { } plan || IsBusy) return;
        var mapping = CategoryRows.ToDictionary(r => r.Key, r => r.Selected.Choice);
        IsBusy = true;
        Progress = 0;
        ProgressMax = Math.Max(1, plan.ToImport);
        try
        {
            var progress = new Progress<int>(n => Progress = n);
            var report = await Task.Run(() => _core.Import.Execute(plan, mapping, progress));
            _store.Save(mapping);
            Result = Res.Format("ImportResult", report.Added, report.DuplicatesSkipped, report.Errors, report.CategoriesCreated);
            Step = 5;
        }
        catch (Core.Sync.UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Message = Res.Format("ImportFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
