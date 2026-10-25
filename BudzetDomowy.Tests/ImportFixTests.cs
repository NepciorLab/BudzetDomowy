using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

/// <summary>Correcting rows of the import preview, the cell address of the income, and the entries behind a place's total.</summary>
public sealed class ImportFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-impfix-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];
    private static readonly DateTime Jan = new(2026, 1, 1);
    private static readonly ImportOptions Options = new() { Today = new DateOnly(2026, 10, 24) };

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    // ----- cell addresses -----

    [Theory]
    [InlineData("E3", 2, 4)]
    [InlineData("e3", 2, 4)]
    [InlineData("$E$3", 2, 4)]
    [InlineData(" A1 ", 0, 0)]
    [InlineData("AA10", 9, 26)]
    [InlineData("Z1", 0, 25)]
    public void Address_IsReadAsRowAndColumn(string text, int row, int column)
    {
        Assert.True(SheetData.TryParseAddress(text, out var r, out var c));
        Assert.Equal((row, column), (r, c));
        Assert.Equal(SheetData.Address(row, column), text.Trim().Replace("$", "").ToUpperInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("3E")]
    [InlineData("E")]
    [InlineData("3")]
    [InlineData("E0")]
    [InlineData("E-1")]
    [InlineData("E3:F4")]
    [InlineData("XFE1")]
    [InlineData("E 3")]
    public void Address_ThatIsNotOne_IsRefused(string text) => Assert.False(SheetData.TryParseAddress(text, out _, out _));

    // ----- income from a cell -----

    [Fact]
    public void IncomeCell_IsUsedWhereTheLabelIsNotFound()
    {
        var sheet = SheetBuilder.MonthSheet("Sty", Jan, 500);                          // "Przychody" in D3, the month's figure in G3

        var without = ImportParser.Parse(Select(sheet).Sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeLabel = "Wpływy" });
        Assert.Empty(without);                                                          // the label is not there, nobody said where else

        var rows = ImportParser.Parse(sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeLabel = "Wpływy", IncomeCell = "g3" });
        var income = Assert.Single(rows);
        Assert.Equal((TransactionType.Income, 500m, ImportRowStatus.New), (income.Type, income.Amount, income.Status));
        Assert.Equal((3, new DateOnly(2026, 1, 1), ImportParser.SheetIncomeCategory), (income.ExcelRow, income.Date, income.SourceCategory));
        Assert.Equal("500", income.RawAmount);
    }

    [Fact]
    public void IncomeCell_IsOnlyAFallback_WhenTheLabelIsFound()
    {
        var sheet = SheetBuilder.MonthSheet("Sty", Jan, 500);
        var rows = ImportParser.Parse(sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeLabel = "Przychody", IncomeCell = "E3" });
        Assert.Equal(500m, Assert.Single(rows).Amount);                                 // the labelled one (G3), not the cumulative E3 (1500)
    }

    [Fact]
    public void IncomeCell_EmptyMeansNoIncome_ButAWordIsReported()
    {
        var sheet = SheetBuilder.MonthSheet("Sty", Jan, 500);
        Assert.Empty(ImportParser.Parse(sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeCell = "H9" }));
        Assert.Empty(ImportParser.Parse(sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeCell = "not an address" }));

        var text = ImportParser.Parse(sheet, Select(sheet).Map, Jan.ToDateOnly(), Options with { IncomeCell = "D3" });          // the word "Przychody"
        var problem = Assert.Single(text);
        Assert.Equal((ImportRowStatus.Error, "income-cell", "Przychody"), (problem.Status, problem.Message, problem.RawAmount));

        var noMonth = ImportParser.Parse(sheet, Select(sheet).Map, null, Options with { IncomeCell = "G3" });
        Assert.Equal(("no-date", 500m), (Assert.Single(noMonth).Message, noMonth[0].Amount));
    }

    // ----- correcting rows -----

    private static SheetData BrokenSheet() => SheetBuilder.MonthSheet("Sty", Jan, null,
        SheetBuilder.Row(null, null, "Kiosk", null, "gazeta", 4.5),                    // no date yet: nothing above gives one
        SheetBuilder.Row(1.0, Jan, "Lewiatan", "Spożywcze", null, "12,3x"),            // an amount that cannot be read
        SheetBuilder.Row(null, null, "Orlen", "Auto", null, -20.0),                    // negative while all rows are expenses
        SheetBuilder.Row(null, null, "Basen", "Pływanie", null, 18.0));                // fine

    [Fact]
    public void ErrorRows_KeepWhatTheCellHeld_ToHelpCorrectingThem()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);

        Assert.Equal([ImportRowStatus.Error, ImportRowStatus.Error, ImportRowStatus.Error, ImportRowStatus.New], plan.Rows.Select(r => r.Status));
        Assert.Equal(["no-date", "bad-amount", "negative", ""], plan.Rows.Select(r => r.Message));
        Assert.Equal("12,3x", plan.Rows[1].RawAmount);
        Assert.Equal(3, plan.Errors);
    }

    [Fact]
    public void Fix_MakesARowImportable_AndTheOthersStayAsTheyWere()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);
        var food = core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;

        var fixedDate = core.Import.Fix(plan, 0, new ImportRowFix(new DateOnly(2026, 1, 5), 4.5m, TransactionType.Expense, " Kiosk ", " gazeta ", food, false), Options.Today);
        var row = fixedDate.Rows[0];
        Assert.Equal((ImportRowStatus.New, "Kiosk", "gazeta", food, true), (row.Status, row.Place, row.Note, row.CategoryId, row.IsEdited));
        Assert.Equal(plan.Rows[1], fixedDate.Rows[1]);                                  // the rest is untouched
        Assert.Equal(2, fixedDate.Errors);

        var noCategory = core.Import.Fix(plan, 1, new ImportRowFix(new DateOnly(2026, 1, 1), 12.3m, TransactionType.Expense, "Lewiatan", "", null, false), Options.Today);
        Assert.Equal(ImportRowStatus.New, noCategory.Rows[1].Status);                   // the file's own category name is still there

        var income = core.Import.Fix(plan, 2, new ImportRowFix(new DateOnly(2026, 1, 1), 20m, TransactionType.Income, "Orlen", "", null, false), Options.Today);
        Assert.Equal(TransactionType.Income, income.Rows[2].Type);
    }

    [Fact]
    public void Fix_ThatIsStillIncomplete_StaysAnError_AndSkipTakesTheRowOut()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);
        ImportRow After(ImportRowFix fix, int index = 0) => core.Import.Fix(plan, index, fix, Options.Today).Rows[index];

        var ok = new ImportRowFix(new DateOnly(2026, 1, 5), 4.5m, TransactionType.Expense, "Kiosk", "", Guid.NewGuid(), false);
        Assert.Equal(("no-date", ImportRowStatus.Error), (After(ok with { Date = null }).Message, After(ok with { Date = null }).Status));
        Assert.Equal("bad-amount", After(ok with { Amount = 0 }).Message);
        Assert.Equal("date-far-future", After(ok with { Date = new DateOnly(2040, 1, 1) }).Message);

        var skipped = After(ok with { Skip = true });
        Assert.Equal((ImportRowStatus.Skipped, "skipped-by-user", false), (skipped.Status, skipped.Message, skipped.WillBeImported));
        var all = core.Import.Fix(plan, 0, ok with { Skip = true }, Options.Today);
        Assert.Equal(2, all.Errors);
        Assert.Equal(plan.ToImport, all.ToImport);
    }

    [Fact]
    public void Fix_ChecksDuplicatesAgain_BothWays()
    {
        var core = NewCore();
        core.Transactions.Add(new DateOnly(2026, 1, 5), TransactionType.Expense, 4.5m, null, "gazeta", null, place: "Kiosk");
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);

        var same = new ImportRowFix(new DateOnly(2026, 1, 5), 4.5m, TransactionType.Expense, "Kiosk", "gazeta", null, false);
        var duplicate = core.Import.Fix(plan, 0, same, Options.Today);
        Assert.Equal((ImportRowStatus.Duplicate, 1), (duplicate.Rows[0].Status, duplicate.Duplicates));

        var other = core.Import.Fix(duplicate, 0, same with { Amount = 5m }, Options.Today);   // corrected again: no longer the same entry
        Assert.Equal((ImportRowStatus.Warning, 0), (other.Rows[0].Status, other.Duplicates));
        Assert.Equal("no-category", other.Rows[0].Message);
    }

    [Fact]
    public void Execute_ImportsTheCorrectedRows_WithTheChosenCategory_AndLeavesTheSkippedOnes()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);
        var transport = core.Reader.GetCategories().First(c => c.Name == "Auto" && c.ParentId is null).Id;
        plan = core.Import.Fix(plan, 0, new ImportRowFix(new DateOnly(2026, 1, 5), 4.5m, TransactionType.Expense, "Kiosk", "gazeta", transport, false), Options.Today);
        plan = core.Import.Fix(plan, 1, new ImportRowFix(null, 12.3m, TransactionType.Expense, "Lewiatan", "", null, Skip: true), Options.Today);

        var report = core.Import.Execute(plan, core.Import.SuggestMapping(plan));

        Assert.Equal(2, report.Added);                                                  // the corrected kiosk and the swimming pool
        var all = core.Reader.GetTransactions();
        var kiosk = all.Single(t => t.Place == "Kiosk");
        Assert.Equal((new DateOnly(2026, 1, 5), 4.5m, transport), (kiosk.Date, kiosk.AmountPln, kiosk.CategoryId));
        Assert.DoesNotContain(all, t => t.Place is "Lewiatan" or "Orlen");
    }

    // ----- the entries behind a place's total -----

    [Fact]
    public void OfPlace_GivesExactlyTheEntriesCountedInTheTotal()
    {
        var all = new List<Transaction>();
        var id = 0;
        Transaction T(string date, string place, decimal amount, TransactionType type = TransactionType.Expense) =>
            new() { Id = Guid.NewGuid(), Date = DateOnly.Parse(date), Place = place, AmountPln = amount, Amount = amount, Type = type, Note = "n" + id++ };
        all.AddRange([
            T("2026-03-01", "Biedronka", 10), T("2026-03-05", "biedronka ", 20), T("2026-03-09", "BIEDRONKA", 5),       // the same place, written three ways
            T("2026-02-28", "Biedronka", 99), T("2026-03-10", "Biedronka", 7, TransactionType.Income),                   // other month / other type
            T("2026-03-02", "Orlen", 40), T("2026-03-03", "", 3), T("2026-03-04", "  ", 4)]);                            // and entries without a place
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 31);

        var totals = ComparisonReports.ByPlace(all, from, to, TransactionType.Expense, "(bez miejsca)");
        var shop = totals.Single(p => p.Name == "Biedronka");
        var entries = ComparisonReports.OfPlace(all, from, to, TransactionType.Expense, shop.Key);

        Assert.Equal(3, entries.Count);
        Assert.Equal(shop.Amount, entries.Sum(t => t.AmountPln));
        Assert.Equal(shop.Count, entries.Count);
        Assert.Equal([new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 1)], entries.Select(t => t.Date));      // newest first

        var none = totals.Single(p => p.Name == "(bez miejsca)");
        var blank = ComparisonReports.OfPlace(all, from, to, TransactionType.Expense, none.Key);
        Assert.Equal(2, blank.Count);
        Assert.Equal(none.Amount, blank.Sum(t => t.AmountPln));

        Assert.Empty(ComparisonReports.OfPlace(all, from, to, TransactionType.Expense, "nie ma takiego"));
    }

    // ----- subcategories in the place dictionary -----

    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Vegetables = Guid.NewGuid();
    private static readonly Guid Meat = Guid.NewGuid();
    private static readonly Guid Bread = Guid.NewGuid();
    private static readonly Guid Fuel = Guid.NewGuid();
    private static readonly Guid Travel = Guid.NewGuid();

    private static Category Cat(Guid id, string name, Guid? parent = null) => new() { Id = id, Name = name, ParentId = parent, Type = CategoryType.Expense };

    private static readonly Category[] Tree =
    [
        Cat(Groceries, "Spożywcze"), Cat(Vegetables, "Warzywa", Groceries), Cat(Meat, "Mięso", Groceries), Cat(Bread, "Pieczywo", Groceries),
        Cat(Travel, "Podróże"), Cat(Fuel, "Paliwo", Travel),
    ];

    private static Transaction Tx(string date, string place, Guid? category) => new()
    {
        Id = Guid.NewGuid(), Date = DateOnly.Parse(date), Type = TransactionType.Expense, Amount = 10, AmountPln = 10, CurrencyCode = "PLN",
        Place = place, CategoryId = category, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Dictionary_PrefersASubcategory_WhenTheMainOneWinsOnlyBecauseTheSubcategoriesSplitTheVotes()
    {
        var history = new[]
        {
            Tx("2026-01-01", "Biedronka", Groceries), Tx("2026-01-02", "Biedronka", Groceries),           // 2 under the main category
            Tx("2026-01-03", "Biedronka", Vegetables), Tx("2026-01-04", "Biedronka", Vegetables), Tx("2026-01-05", "Biedronka", Vegetables),
            Tx("2026-01-06", "Biedronka", Meat), Tx("2026-01-07", "Biedronka", Meat),
        };

        var flat = PlaceCategoryDictionary.Build(history, TransactionType.Expense).Suggest("Biedronka");
        Assert.Equal([Vegetables, Meat, Groceries], flat);                                          // without the tree: the most used, then the most recent

        var withTree = PlaceCategoryDictionary.Build(history, TransactionType.Expense, Tree).Suggest("biedronka");
        Assert.Equal(Vegetables, withTree[0]);                                                      // the most used subcategory
        Assert.Equal([Vegetables, Meat, Groceries], withTree);                                      // the rest keeps its order
    }

    [Fact]
    public void Dictionary_AMainCategoryWithMoreBookingsThanItsSubcategories_StillGivesWayToTheSubcategory()
    {
        var history = Enumerable.Range(1, 6).Select(i => Tx($"2026-01-{i:00}", "Lidl", Groceries))
            .Concat([Tx("2026-02-01", "Lidl", Bread), Tx("2026-02-02", "Lidl", Bread)]).ToList();

        Assert.Equal(Bread, PlaceCategoryDictionary.Build(history, TransactionType.Expense, Tree).Suggest("Lidl")[0]);
        Assert.Equal(Groceries, PlaceCategoryDictionary.Build(history, TransactionType.Expense).Suggest("Lidl")[0]);   // as before without the tree
    }

    [Fact]
    public void Dictionary_TheMainCategoryItselfStaysWhenThePlaceNeverUsedASubcategory_AndTheBiggerBranchWins()
    {
        var history = new[]
        {
            Tx("2026-01-01", "Orlen", Travel), Tx("2026-01-02", "Orlen", Travel),                  // the main category only
            Tx("2026-01-03", "Orlen", Groceries), Tx("2026-01-04", "Orlen", Vegetables),          // another branch with two bookings
            Tx("2026-01-05", "Orlen", Fuel), Tx("2026-01-06", "Orlen", Fuel),                      // the travel branch: 4 bookings in all
        };

        var suggestions = PlaceCategoryDictionary.Build(history, TransactionType.Expense, Tree).Suggest("Orlen");
        Assert.Equal(Fuel, suggestions[0]);                                                         // Travel (4) beats Groceries (2); its subcategory first

        var only = new[] { Tx("2026-01-01", "Kiosk", Travel), Tx("2026-01-02", "Kiosk", Travel), Tx("2026-01-03", "Kiosk", Groceries) };
        Assert.Equal(Travel, PlaceCategoryDictionary.Build(only, TransactionType.Expense, Tree).Suggest("Kiosk")[0]);   // no subcategory used: the main one
    }

    [Fact]
    public void Dictionary_NextSuggestionsStillSkipWhatTheEntryAlreadyUses_AndPartialPlacesFollowTheSameRule()
    {
        var history = new[]
        {
            Tx("2026-01-01", "Biedronka Kraków", Groceries), Tx("2026-01-02", "Biedronka Kraków", Groceries),
            Tx("2026-01-03", "Biedronka Kraków", Meat), Tx("2026-01-04", "Biedronka Kraków", Meat),
        };
        var dictionary = PlaceCategoryDictionary.Build(history, TransactionType.Expense, Tree);

        Assert.Equal(Meat, dictionary.SuggestNext("Biedronka Kraków", []));
        Assert.Equal(Groceries, dictionary.SuggestNext("Biedronka Kraków", [Meat]));
        Assert.Equal(Meat, dictionary.Suggest("Biedr")[0]);                                         // a prefix (three letters or more) is arranged the same way
    }

    // ----- an income never gets an expense category -----

    [Fact]
    public void Execute_LeavesARowUncategorised_WhenItsTypeWasChangedAndTheCategoryDoesNotFit()
    {
        var core = NewCore();
        var plan = core.Import.Plan([Select(BrokenSheet())], Options);
        var food = core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;
        var salary = core.Reader.GetCategories().First(c => c.Type == CategoryType.Income && c.ParentId is null).Id;

        // the kiosk row becomes an income: the expense category chosen for it is dropped, an income category is kept
        plan = core.Import.Fix(plan, 0, new ImportRowFix(new DateOnly(2026, 1, 5), 4.5m, TransactionType.Income, "Kiosk", "gazeta", food, false), Options.Today);
        plan = core.Import.Fix(plan, 1, new ImportRowFix(new DateOnly(2026, 1, 6), 9m, TransactionType.Income, "Zwrot", "", salary, false), Options.Today);
        core.Import.Execute(plan, core.Import.SuggestMapping(plan));

        var all = core.Reader.GetTransactions();
        Assert.Null(all.Single(t => t.Place == "Kiosk").CategoryId);
        Assert.Equal(salary, all.Single(t => t.Place == "Zwrot").CategoryId);
    }
}

internal static class DateExtensions
{
    public static DateOnly ToDateOnly(this DateTime d) => DateOnly.FromDateTime(d);
}
