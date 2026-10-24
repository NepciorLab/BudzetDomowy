using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using ClosedXML.Excel;

namespace BudzetDomowy.Tests;

/// <summary>Builds worksheets shaped like the user's monthly sheets (day number + date in the first row of a day, headings in row 6).</summary>
internal static class SheetBuilder
{
    public static SheetData MonthSheet(string name, DateTime monthStart, double? income, params object?[][] entries)
    {
        var rows = new List<object?[]>
        {
            new object?[] { null, null, null, null, "Narastająco", "Średnio", "w miesiącu" },
            new object?[] { null, monthStart, null, "Wydatki", 0.0, 0.0, 0.0 },
            // the first figure is cumulative for the year, the one under "w miesiącu" is the month's own
            new object?[] { null, null, null, "Przychody", income * 3, null, income },
            new object?[] { null, null, null, "Bilans" },
            new object?[] { null, null, null, "Spożywcze" },
            new object?[] { "Data", null, "Gdzie", "Kategoria", "Opis", "Kwota", "Narastająco" },
        };
        rows.AddRange(entries);
        return new SheetData(name, rows.ToArray());
    }

    /// <summary>[day number, date (only on the first row of a day), place, category, note, amount]</summary>
    public static object?[] Row(object? day, DateTime? date, string? place, string? category, string? note, object? amount) =>
        [day, date, place, category, note, amount, null];
}

public class ImportAnalyzerTests
{
    private static readonly DateTime Jan = new(2026, 1, 1);

    private static SheetData Sample() => SheetBuilder.MonthSheet("Sty", Jan, 500,
        SheetBuilder.Row(1.0, Jan, "Lewiatan", "Spożywcze", null, 10.5),
        SheetBuilder.Row(null, null, "Piekarnia", "Spożywcze", "chleb", 4.0),
        SheetBuilder.Row(2.0, Jan.AddDays(1), "Orlen", "Auto", null, 200.0));

    [Fact]
    public void FindsTheHeadingRow_AndTheRealDateColumn_NotTheDayNumbers()
    {
        var a = ImportAnalyzer.Analyze(Sample());

        Assert.True(a.LooksLikeData);
        var map = a.Map!;
        Assert.Equal(5, map.HeaderRow);                  // row 6 in Excel
        Assert.Equal(1, map.Date);                       // column B holds the dates, A only the day numbers under "Data"
        Assert.Equal((2, 3, 4, 5), (map.Place!.Value, map.Category!.Value, map.Note!.Value, map.Amount!.Value));
        Assert.Equal(3, a.DataRows);
        Assert.Equal(new DateOnly(2026, 1, 1), a.SheetMonth);
    }

    [Fact]
    public void SheetsWithoutTransactions_AreNotOffered()
    {
        var stats = new SheetData("Statystyki", [["Miesiąc", "Przychody", "Wydatki"], ["Styczeń", 500.0, 100.0]]);
        var vacation = new SheetData("Podsumowanie wakacji", [["Gdzie", "Cel", "Kwota", "Kurs", "Kwota PLN"], ["Hotel", "Rachunki", 100.0, 4.2, 420.0]]);
        var empty = new SheetData("Pusty", []);

        Assert.False(ImportAnalyzer.Analyze(stats).LooksLikeData);
        Assert.False(ImportAnalyzer.Analyze(vacation).LooksLikeData);   // an amount and a place, but no dates
        Assert.False(ImportAnalyzer.Analyze(empty).LooksLikeData);
    }

    [Fact]
    public void ABankStyleSheet_WithATextDateColumn_IsRecognised()
    {
        var bank = new SheetData("Wyciąg",
        [
            ["Data", "Opis", "Kwota", "Typ"],
            ["2026-03-01", "Pensja", 5000.0, "Przychód"],
            ["02.03.2026", "Biedronka", -45.5, null],
        ]);
        var a = ImportAnalyzer.Analyze(bank);
        Assert.True(a.LooksLikeData);
        Assert.Equal(0, a.Map!.Date);
        Assert.Equal(3, a.Map.Type);
        Assert.Equal(2, a.DataRows);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(5, "F")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    public void ColumnLetters(int index, string expected) => Assert.Equal(expected, SheetData.ColumnLetter(index));

    [Fact]
    public void TextNormalisation_IgnoresCaseDiacriticsAndSpaces()
    {
        Assert.Equal("ubrania/buty", TextNorm.Normalize("  Ubrania/Buty "));
        Assert.Equal("spozywcze", TextNorm.Normalize("SPOŻYWCZE"));
        Assert.Equal("lodz", TextNorm.Normalize("Łódź"));
        Assert.Equal("a b", TextNorm.Normalize("a    b"));
    }
}

public class ImportParserTests
{
    private static readonly DateTime Jan = new(2026, 1, 1);
    private static readonly ImportOptions Options = new() { Today = new DateOnly(2026, 10, 24) };

    private static IReadOnlyList<ImportRow> Parse(SheetData sheet, ImportOptions? options = null)
    {
        var a = ImportAnalyzer.Analyze(sheet);
        return ImportParser.Parse(sheet, a.Map!, a.SheetMonth, options ?? Options);
    }

    [Fact]
    public void RowsWithoutADate_InheritTheDateOfTheDayAbove()
    {
        var rows = Parse(SheetBuilder.MonthSheet("Sty", Jan, null,
            SheetBuilder.Row(1.0, Jan, "A", "Spożywcze", null, 1.0),
            SheetBuilder.Row(null, null, "B", "Spożywcze", null, 2.0),
            SheetBuilder.Row(2.0, Jan.AddDays(1), "C", "Auto", null, 3.0),
            SheetBuilder.Row(null, null, "D", "Auto", null, 4.0)));

        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 2)], rows.Select(r => r.Date!.Value));
        Assert.All(rows, r => Assert.Equal(TransactionType.Expense, r.Type));
        Assert.Equal([1m, 2m, 3m, 4m], rows.Select(r => r.Amount));
    }

    [Fact]
    public void EntriesWithoutCategory_ArePossibleButFlagged_AndAmountOnlyRowsAreKept()
    {
        var rows = Parse(SheetBuilder.MonthSheet("Sty", Jan, null,
            SheetBuilder.Row(1.0, Jan, "Lewiatan", null, null, 5.0),
            SheetBuilder.Row(2.0, Jan.AddDays(1), null, null, null, 1.0)));

        Assert.All(rows, r => Assert.Equal(ImportRowStatus.Warning, r.Status));
        Assert.All(rows, r => Assert.True(r.WillBeImported));
        Assert.Equal("", rows[1].Place);
    }

    [Fact]
    public void SubtotalRows_RepeatedHeadings_AndEmptyRows_AreNotTransactions()
    {
        var rows = Parse(SheetBuilder.MonthSheet("Sty", Jan, null,
            SheetBuilder.Row(1.0, Jan, "Lewiatan", "Spożywcze", null, 5.0),
            SheetBuilder.Row(null, null, "Razem", null, null, 5.0),
            SheetBuilder.Row(null, null, null, "Suma częściowa", null, 5.0),
            SheetBuilder.Row(null, null, null, null, null, null),
            SheetBuilder.Row(null, null, "Gdzie", "Kategoria", "Opis", "Kwota"),
            SheetBuilder.Row(null, null, "Kolejny", "Auto", null, 0.0)));

        Assert.Equal(1, rows.Count(r => r.WillBeImported));
        Assert.Equal(3, rows.Count(r => r.Status == ImportRowStatus.Skipped));   // two subtotals and the zero amount
        Assert.DoesNotContain(rows, r => r.Status == ImportRowStatus.Error);
    }

    [Fact]
    public void TextAmounts_AreParsed_AndProblemsAreReportedPerRow()
    {
        var rows = Parse(SheetBuilder.MonthSheet("Sty", Jan, null,
            SheetBuilder.Row(1.0, Jan, "A", "X", null, "12,50 zł"),
            SheetBuilder.Row(null, null, "B", "X", null, "1 234,56"),
            SheetBuilder.Row(null, null, "C", "X", null, "abc"),
            SheetBuilder.Row(null, null, "D", "X", null, -7.0),
            SheetBuilder.Row(null, null, "E", "X", null, "9 EUR"),
            SheetBuilder.Row(3.0, new DateTime(2040, 1, 1), "F", "X", null, 1.0)));

        Assert.Equal([12.5m, 1234.56m], rows.Where(r => r.WillBeImported).Select(r => r.Amount));
        Assert.Equal(["bad-amount", "negative", "foreign-currency", "date-far-future"], rows.Where(r => r.Status == ImportRowStatus.Error).Select(r => r.Message));
    }

    [Fact]
    public void NegativeIsExpense_ReadsABankStyleSheet()
    {
        var bank = new SheetData("Wyciąg",
        [
            ["Data", "Opis", "Kwota", "Typ"],
            [new DateTime(2026, 3, 1), "Pensja", 5000.0, "Przychód"],
            [new DateTime(2026, 3, 2), "Biedronka", -45.5, null],
            [new DateTime(2026, 3, 3), "Zwrot", 10.0, null],
        ]);
        var rows = Parse(bank, Options with { Sign = SignMode.NegativeIsExpense });

        Assert.Equal([TransactionType.Income, TransactionType.Expense, TransactionType.Income], rows.Select(r => r.Type));
        Assert.Equal([5000m, 45.5m, 10m], rows.Select(r => r.Amount));
    }

    [Fact]
    public void ALabelledIncomeCell_BecomesOneIncomeEntryOnTheFirstOfTheMonth()
    {
        var sheet = SheetBuilder.MonthSheet("Mar", new DateTime(2026, 3, 1), 500,
            SheetBuilder.Row(1.0, new DateTime(2026, 3, 1), "A", "X", null, 1.0));
        var withIncome = Parse(sheet, Options with { IncomeLabel = "Przychody" });
        var income = Assert.Single(withIncome, r => r.Type == TransactionType.Income);
        Assert.Equal(new DateOnly(2026, 3, 1), income.Date);
        Assert.Equal(500m, income.Amount);
        Assert.True(income.IsFromSheetIncome);

        Assert.DoesNotContain(Parse(sheet), r => r.Type == TransactionType.Income);
    }
}

public class ImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-imp-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];
    private static readonly DateTime Jan = new(2026, 1, 1);
    private static readonly ImportOptions Options = new() { Today = new DateOnly(2026, 10, 24), IncomeLabel = "Przychody" };

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        var c = BudgetCore.Create(Path.Combine(_root, "l" + _cores.Count), Path.Combine(_root, "shared"), "x", rateSource: new FakeRateSource());
        _cores.Add(c);
        c.Categories.SeedIfEmpty();
        return c;
    }

    private static SheetSelection Select(SheetData sheet)
    {
        var a = ImportAnalyzer.Analyze(sheet);
        return new SheetSelection(sheet, a.Map!, a.SheetMonth);
    }

    private static SheetData Month() => SheetBuilder.MonthSheet("Sty", Jan, 500,
        SheetBuilder.Row(1.0, Jan, "Lewiatan", "Spożywcze", null, 1.0),
        SheetBuilder.Row(null, null, "Lewiatan", "Spożywcze", null, 1.0),     // the very same purchase twice on one day
        SheetBuilder.Row(null, null, "Orlen", "Paliwo dla łodzi", "pełny bak", 200.0),
        SheetBuilder.Row(2.0, Jan.AddDays(1), "Kościół", "ubrania/BUTY", null, 5.0));

    [Fact]
    public void Mapping_UsesExistingCategoriesByName_AndProposesNewOnesForTheRest()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(Month())], Options);
        var map = core.Import.SuggestMapping(plan);

        var food = core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null);
        Assert.Equal(food.Id, map["spozywcze"].ExistingId);
        Assert.Equal(core.Reader.GetCategories().First(c => c.Name == "Ubrania/Buty").Id, map["ubrania/buty"].ExistingId);
        Assert.Equal("Paliwo dla łodzi", map["paliwo dla lodzi"].NewName);
        Assert.Equal("Inne przychody", core.Reader.GetCategories().First(c => c.Id == map[TextNorm.Normalize(ImportParser.SheetIncomeCategory)].ExistingId).Name);
    }

    [Fact]
    public void Import_AddsEntries_CreatesCategories_AndKeepsRealRepeats()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(Month())], Options);
        var map = core.Import.SuggestMapping(plan);
        var report = core.Import.Execute(plan, map);

        Assert.Equal(5, report.Added);                 // 4 purchases (two identical ones kept) + the income of the month
        Assert.Equal(1, report.CategoriesCreated);
        var all = core.Reader.GetTransactions();
        Assert.Equal(2, all.Count(t => t.Place == "Lewiatan" && t.AmountPln == 1m));
        var fuel = all.Single(t => t.Place == "Orlen");
        Assert.Equal("pełny bak", fuel.Note);
        Assert.Equal("Paliwo dla łodzi", core.Reader.GetCategories().First(c => c.Id == fuel.CategoryId).Name);
        Assert.Equal(TransactionType.Income, all.Single(t => t.Type == TransactionType.Income).Type);
        Assert.Equal(new DateOnly(2026, 1, 2), all.Single(t => t.Place == "Kościół").Date);
    }

    [Fact]
    public void ImportingTheSameFileAgain_AddsNothing()
    {
        var core = NewCore();
        var first = core.Import.Plan([Select(Month())], Options);
        core.Import.Execute(first, core.Import.SuggestMapping(first));
        var countAfterFirst = core.Reader.GetTransactions().Count;

        var second = core.Import.Plan([Select(Month())], Options);
        Assert.Equal(0, second.ToImport);
        Assert.Equal(5, second.Duplicates);
        var report = core.Import.Execute(second, core.Import.SuggestMapping(second));
        Assert.Equal(0, report.Added);
        Assert.Equal(countAfterFirst, core.Reader.GetTransactions().Count);
        Assert.Single(core.Reader.GetCategories().Where(c => c.Name == "Paliwo dla łodzi")); // no second copy of the new category
    }

    [Fact]
    public void ADuplicateIsCountedNotMerged_ThreeInTheFile_TwoAlreadyKnown_OneIsAdded()
    {
        var core = NewCore();
        var date = new DateOnly(2026, 1, 1);
        core.Transactions.Add(date, TransactionType.Expense, 1m, null, "", null, place: "Lewiatan");
        core.Transactions.Add(date, TransactionType.Expense, 1m, null, "", null, place: "Lewiatan");

        var sheet = SheetBuilder.MonthSheet("Sty", Jan, null,
            SheetBuilder.Row(1.0, Jan, "Lewiatan", "Spożywcze", null, 1.0),
            SheetBuilder.Row(null, null, "Lewiatan", "Spożywcze", null, 1.0),
            SheetBuilder.Row(null, null, "lewiatan ", "Spożywcze", null, 1.0));
        var plan = core.Import.Plan([Select(sheet)], new ImportOptions { Today = Options.Today });

        Assert.Equal(1, plan.ToImport);
        Assert.Equal(2, plan.Duplicates);
    }

    [Fact]
    public void RememberedMapping_WinsOverTheSuggestion()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(Month())], Options);
        var home = core.Reader.GetCategories().First(c => c.Name == "Dla domu" && c.ParentId is null);
        var remembered = new Dictionary<string, CategoryChoice> { ["spozywcze"] = new CategoryChoice(home.Id, null) };

        var map = core.Import.SuggestMapping(plan, remembered);
        Assert.Equal(home.Id, map["spozywcze"].ExistingId);
        Assert.Equal("Paliwo dla łodzi", map["paliwo dla lodzi"].NewName);

        // a remembered category that no longer exists falls back to the suggestion
        var stale = new Dictionary<string, CategoryChoice> { ["spozywcze"] = new CategoryChoice(Guid.NewGuid(), null) };
        Assert.NotEqual(stale["spozywcze"].ExistingId, core.Import.SuggestMapping(plan, stale)["spozywcze"].ExistingId);
    }

    [Fact]
    public void ChoosingUncategorized_ImportsWithoutACategory()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(Month())], Options);
        var map = core.Import.SuggestMapping(plan).ToDictionary(kv => kv.Key, _ => CategoryChoice.Uncategorized);
        core.Import.Execute(plan, map);
        Assert.All(core.Reader.GetTransactions(), t => Assert.Null(t.CategoryId));
        Assert.DoesNotContain(core.Reader.GetCategories(), c => c.Name == "Paliwo dla łodzi");
    }

    [Fact]
    public void ImportedEntries_SyncToTheOtherComputer()
    {
        var a = NewCore();
        var b = NewCore();
        var plan = a.Import.Plan([Select(Month())], Options);
        a.Import.Execute(plan, a.Import.SuggestMapping(plan));
        a.Sync.SyncOnce(); b.Sync.SyncOnce();
        Assert.Equal(a.Reader.GetTransactions().Count, b.Reader.GetTransactions().Count);
    }
}

public class WorkbookLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-xl-" + Guid.NewGuid().ToString("N"));

    public WorkbookLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ReadsDatesNumbersAndText_AndEverySheet()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sty");
            ws.Cell(1, 1).Value = "Data";
            ws.Cell(1, 2).Value = "Kwota";
            ws.Cell(2, 1).Value = new DateTime(2026, 1, 5);
            ws.Cell(2, 2).Value = 12.5;
            ws.Cell(3, 1).Value = "tekst";
            wb.AddWorksheet("Pusty");
            wb.SaveAs(path);
        }

        var sheets = WorkbookLoader.Load(path);
        Assert.Equal(["Sty", "Pusty"], sheets.Select(s => s.Name));
        var s = sheets[0];
        Assert.Equal(new DateTime(2026, 1, 5), s.At(1, 0));
        Assert.Equal(12.5, s.At(1, 1));
        Assert.Equal("tekst", s.At(2, 0));
        Assert.Null(s.At(2, 1));
        Assert.Equal(0, sheets[1].RowCount);
    }

    [Fact]
    public void OpensAFileThatIsOpenElsewhere_ReadOnly()
    {
        var path = Path.Combine(_dir, "locked.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("A").Cell(1, 1).Value = 1;
            wb.SaveAs(path);
        }
        using var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Single(WorkbookLoader.Load(path));
    }

    /// <summary>Runs against the user's real workbook when it is present (it is never part of the repository).</summary>
    [Fact]
    public void TheRealSampleWorkbook_IsRecognisedAndImportsWithoutErrors()
    {
        var path = FindSample();
        if (path is null) return;

        var sheets = WorkbookLoader.Load(path);
        var analyses = ImportAnalyzer.AnalyzeAll(sheets);
        var data = analyses.Where(a => a.LooksLikeData).ToList();

        Assert.Equal(12, data.Count);                                    // Sty..Gru
        Assert.DoesNotContain(analyses, a => a.LooksLikeData && a.Name is "Statystyki" or "Wyszukiwanie" or "Podsumowanie wakacji");
        Assert.All(data, a => Assert.Equal(1, a.Map!.Date));
        Assert.Equal(1282, data.Sum(a => a.DataRows));

        var selections = data.Select(a => new SheetSelection(sheets.First(s => s.Name == a.Name), a.Map!, a.SheetMonth)).ToList();
        var parsed = selections.SelectMany(s => ImportParser.Parse(s.Sheet, s.Map, s.SheetMonth, new ImportOptions { IncomeLabel = "Przychody" })).ToList();
        Assert.Equal(1282, parsed.Count(r => r.WillBeImported && r.Type == TransactionType.Expense));
        Assert.Equal(12, parsed.Count(r => r.Type == TransactionType.Income));
        Assert.All(parsed.Where(r => r.Type == TransactionType.Income), r => Assert.Equal(500m, r.Amount));   // the month's value, not the running total
        Assert.DoesNotContain(parsed, r => r.Status == ImportRowStatus.Error);
        Assert.Equal(1, parsed.Where(r => r.Sheet == "Sty").Min(r => r.Date!.Value).Month);
    }

    private static string? FindSample()
    {
        var env = Environment.GetEnvironmentVariable("BUDZET_SAMPLE_XLSM");
        if (env is { Length: > 0 } && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "_sample", "Wydatki_2026.xlsm");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
