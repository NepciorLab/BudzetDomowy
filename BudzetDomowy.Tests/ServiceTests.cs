using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Tests;

public class AmountParserTests
{
    [Theory]
    [InlineData("12,50", 12.50)]
    [InlineData("12.50", 12.50)]
    [InlineData("12", 12)]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1\u00A0234,56 zł", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("  7,5 PLN ", 7.5)]
    [InlineData("0,005", 0.01)]
    public void Parses(string text, double expected)
    {
        Assert.True(AmountParser.TryParse(text, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1,2,3")]
    [InlineData("1.2.3")]
    [InlineData("12 EUR")]
    public void Rejects(string text) => Assert.False(AmountParser.TryParse(text, out _));
}

public class CategoryHelpersTests
{
    [Fact]
    public void DefaultCategories_AreConsistent()
    {
        var all = DefaultCategories.Create();
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        Assert.All(all.Where(c => c.ParentId is not null), c => Assert.Contains(all, p => p.Id == c.ParentId));
        Assert.Contains(all, c => c.Name == "Spożywcze" && c.ParentId is null);
        Assert.Contains(all, c => c.Type == CategoryType.Income);
        // Ids are stable between calls (needed for concurrent seeding on two devices).
        Assert.Equal(all.Select(c => c.Id), DefaultCategories.Create().Select(c => c.Id));
    }

    [Fact]
    public void Flatten_ListsParentsBeforeChildren_AndFiltersByTypeAndArchive()
    {
        var all = DefaultCategories.Create().ToList();
        var archived = all.First(c => c.Name == "Paliwo");
        all[all.IndexOf(archived)] = archived with { IsArchived = true };

        var options = CategoryHelpers.ForEntry(all, TransactionType.Expense);
        var names = options.Select(o => o.DisplayName).ToList();

        Assert.True(names.IndexOf("Auto") < names.IndexOf("Auto › Serwis i naprawy"));
        Assert.DoesNotContain("Auto › Paliwo", names);
        Assert.DoesNotContain("Wynagrodzenie", names);
        Assert.Contains("Wynagrodzenie", CategoryHelpers.ForEntry(all, TransactionType.Income).Select(o => o.DisplayName));
    }

    [Fact]
    public void WithDescendants_IncludesTheWholeSubtree()
    {
        var all = DefaultCategories.Create();
        var auto = all.First(c => c.Name == "Auto");
        var ids = CategoryHelpers.WithDescendants(auto.Id, all);
        Assert.Equal(1 + all.Count(c => c.ParentId == auto.Id), ids.Count);
    }

    [Fact]
    public void ActiveEvent_IsTheOneContainingTheDate_LatestStartWins()
    {
        var a = new SpecialEvent { Id = Guid.NewGuid(), Name = "Rok", DateFrom = new DateOnly(2026, 1, 1), DateTo = new DateOnly(2026, 12, 31) };
        var b = new SpecialEvent { Id = Guid.NewGuid(), Name = "Wakacje", DateFrom = new DateOnly(2026, 7, 1), DateTo = new DateOnly(2026, 7, 14) };
        Assert.Equal(b.Id, SpecialEvents.ActiveOn([a, b], new DateOnly(2026, 7, 5))!.Id);
        Assert.Equal(a.Id, SpecialEvents.ActiveOn([a, b], new DateOnly(2026, 8, 1))!.Id);
        Assert.Null(SpecialEvents.ActiveOn([b], new DateOnly(2026, 8, 1)));
    }
}

public class CoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-svc-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    private string Shared => Path.Combine(_root, "shared");

    private BudgetCore NewCore(string name)
    {
        Directory.CreateDirectory(Shared);
        var c = BudgetCore.Create(Path.Combine(_root, name), Shared, name);
        _cores.Add(c);
        return c;
    }

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void SeedingOnTwoDevicesAtTheSameTime_DoesNotDuplicateCategories()
    {
        var a = NewCore("a");
        var b = NewCore("b");
        Assert.True(a.Categories.SeedIfEmpty());
        Assert.True(b.Categories.SeedIfEmpty()); // b had not synced yet
        a.Sync.SyncOnce(); b.Sync.SyncOnce(); a.Sync.SyncOnce();

        Assert.Equal(DefaultCategories.Create().Count, a.Reader.GetCategories().Count);
        Assert.Equal(a.Reader.GetCategories().Count, b.Reader.GetCategories().Count);
    }

    [Fact]
    public void SecondDeviceThatSyncedFirst_DoesNotSeedAgain()
    {
        var a = NewCore("a");
        a.Categories.SeedIfEmpty();
        var b = NewCore("b");
        b.Sync.SyncOnce();
        Assert.False(b.Categories.SeedIfEmpty());
    }

    [Fact]
    public void AddEditDeleteRestore_Transaction()
    {
        var c = NewCore("a");
        c.Categories.SeedIfEmpty();
        var cat = c.Reader.GetCategories().First(x => x.Name == "Spożywcze");

        var t = c.Transactions.Add(new DateOnly(2026, 5, 2), TransactionType.Expense, 23.40m, cat.Id, "  Lewiatan ", null);
        var stored = Assert.Single(c.Reader.GetTransactions());
        Assert.Equal("Lewiatan", stored.Note);
        Assert.Equal(23.40m, stored.AmountPln);
        Assert.Equal(c.Device.DeviceId, stored.CreatedByDeviceId);
        Assert.Single(stored.Lines);

        c.Transactions.Update(stored with { Amount = 30m, Note = "Lewiatan 2" });
        var edited = c.Reader.GetTransaction(t.Id)!;
        Assert.Equal(30m, edited.AmountPln);
        Assert.Equal(30m, edited.Lines[0].Amount);
        Assert.Equal("Lewiatan 2", edited.Lines[0].Description);

        c.Transactions.Delete([edited]);
        Assert.Empty(c.Reader.GetTransactions());
        c.Transactions.Restore([edited]);
        Assert.Equal("Lewiatan 2", Assert.Single(c.Reader.GetTransactions()).Note);
    }

    [Fact]
    public void Place_IsStoredSeparatelyFromTheNote_AndSurvivesEditing()
    {
        var c = NewCore("a");
        var t = c.Transactions.Add(new DateOnly(2026, 5, 2), TransactionType.Expense, 10m, null, "dla mamy", null, place: "  Lewiatan ");
        var stored = c.Reader.GetTransaction(t.Id)!;
        Assert.Equal("Lewiatan", stored.Place);
        Assert.Equal("dla mamy", stored.Note);

        c.Transactions.Update(stored with { Place = "Biedronka" });
        var edited = c.Reader.GetTransaction(t.Id)!;
        Assert.Equal("Biedronka", edited.Place);
        Assert.Equal("dla mamy", edited.Note);
    }

    [Fact]
    public void Database_IsAtTheLatestSchemaVersion()
    {
        var c = NewCore("a");
        using var conn = c.Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal((long)BudzetDomowy.Core.Storage.LocalDatabase.CurrentVersion, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Add_RejectsNonPositiveAmounts()
    {
        var c = NewCore("a");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            c.Transactions.Add(new DateOnly(2026, 1, 1), TransactionType.Expense, 0m, null, "", null));
    }

    [Fact]
    public void BulkCategoryChange_ReturnsPreviousVersions_ForUndo()
    {
        var c = NewCore("a");
        c.Categories.SeedIfEmpty();
        var cats = c.Reader.GetCategories();
        var food = cats.First(x => x.Name == "Spożywcze").Id;
        var home = cats.First(x => x.Name == "Dla domu").Id;
        var t1 = c.Transactions.Add(new DateOnly(2026, 5, 2), TransactionType.Expense, 5m, food, "a", null);
        var t2 = c.Transactions.Add(new DateOnly(2026, 5, 3), TransactionType.Expense, 6m, food, "b", null);

        var before = c.Transactions.ChangeCategory([t1.Id, t2.Id], home);
        Assert.All(c.Reader.GetTransactions(), t => Assert.Equal(home, t.CategoryId));
        Assert.All(c.Reader.GetTransactions(), t => Assert.Equal(home, t.Lines[0].CategoryId));

        c.Transactions.Restore(before);
        Assert.All(c.Reader.GetTransactions(), t => Assert.Equal(food, t.CategoryId));
    }

    [Fact]
    public void Rules_AreLearnedStrengthenedReplacedAndSyncedToTheOtherDevice()
    {
        var a = NewCore("a");
        var b = NewCore("b");
        a.Categories.SeedIfEmpty();
        var cats = a.Reader.GetCategories();
        var food = cats.First(x => x.Name == "Spożywcze").Id;
        var chem = cats.First(x => x.Name == "Chemia").Id;

        Assert.Null(a.Rules.Suggest("Lewiatan"));
        a.Rules.Learn("Lewiatan", food, null);
        a.Rules.Learn("  LEWIATAN ", food, null);
        var rule = Assert.Single(a.Reader.GetCategoryRules());
        Assert.Equal("lewiatan", rule.Pattern);
        Assert.Equal(2, rule.UseCount);

        Assert.Equal(food, a.Rules.Suggest("lewiatan")!.CategoryId);
        Assert.Equal(food, a.Rules.Suggest("Lewiatan ul. Długa")!.CategoryId); // pattern contained in the note

        a.Rules.Learn("Lewiatan", chem, null); // user changed their mind
        Assert.Equal(chem, a.Rules.Suggest("Lewiatan")!.CategoryId);
        Assert.Equal(1, Assert.Single(a.Reader.GetCategoryRules()).UseCount);

        a.Sync.SyncOnce(); b.Sync.SyncOnce();
        Assert.Equal(chem, b.Rules.Suggest("lewiatan")!.CategoryId);
    }

    [Fact]
    public void Rules_PreferTheLongestMatchingPattern_AndIgnoreShortNotes()
    {
        var food = Guid.NewGuid();
        var cake = Guid.NewGuid();
        var rules = new[]
        {
            new CategoryRule { Id = Guid.NewGuid(), Pattern = "lewiatan", CategoryId = food, UseCount = 9 },
            new CategoryRule { Id = Guid.NewGuid(), Pattern = "lewiatan ciasto", CategoryId = cake, UseCount = 1 },
        };
        Assert.Equal(cake, CategoryRuleService.Suggest("Lewiatan ciasto drożdżowe", rules)!.CategoryId);
        Assert.Equal(food, CategoryRuleService.Suggest("lewiatan", rules)!.CategoryId);
        Assert.Null(CategoryRuleService.Suggest("ab", rules));
        Assert.Null(CategoryRuleService.Suggest(null, rules));
    }

    [Fact]
    public void RemovingACategory_DeletesWhenUnused_ArchivesWhenUsed()
    {
        var c = NewCore("a");
        var unused = c.Categories.Add("Nowa", null, CategoryType.Expense);
        var used = c.Categories.Add("Używana", null, CategoryType.Expense);
        var parent = c.Categories.Add("Rodzic", null, CategoryType.Expense);
        c.Categories.Add("Dziecko", parent.Id, CategoryType.Expense);
        c.Transactions.Add(new DateOnly(2026, 5, 2), TransactionType.Expense, 5m, used.Id, "x", null);

        Assert.Equal(CategoryRemoval.Deleted, c.Categories.Remove(unused.Id));
        Assert.Equal(CategoryRemoval.Archived, c.Categories.Remove(used.Id));
        Assert.Equal(CategoryRemoval.Archived, c.Categories.Remove(parent.Id));

        var left = c.Reader.GetCategories();
        Assert.DoesNotContain(left, x => x.Id == unused.Id);
        Assert.True(left.First(x => x.Id == used.Id).IsArchived);
        Assert.True(left.First(x => x.Id == parent.Id).IsArchived);
        Assert.Equal(1, c.Reader.CountTransactionsInCategory(used.Id));
    }

    [Fact]
    public void PeriodArrows_LeadOnlyToPeriodsWithEntries_AndToThePresentOneOnceItHasBegun()
    {
        var c = NewCore("a");
        c.Transactions.Add(new DateOnly(2026, 3, 10), TransactionType.Expense, 5m, null, "x", null);
        c.Transactions.Add(new DateOnly(2026, 6, 20), TransactionType.Expense, 5m, null, "x", null);
        var today = new DateOnly(2026, 9, 15);
        DateOnly? Go(PeriodKind kind, string anchor, int direction) => PeriodNavigation.Target(kind, DateOnly.Parse(anchor), direction, c.Reader, today);

        Assert.Equal(new DateOnly(2026, 6, 20), Go(PeriodKind.Month, "2026-03-01", +1));      // April and May have nothing: leaped over
        Assert.Equal(today, Go(PeriodKind.Month, "2026-06-01", +1));                          // then the present month, which has begun
        Assert.Null(Go(PeriodKind.Month, "2026-09-01", +1));                                  // and nothing after it
        Assert.Equal(new DateOnly(2026, 3, 10), Go(PeriodKind.Month, "2026-06-01", -1));
        Assert.Null(Go(PeriodKind.Month, "2026-03-01", -1));                                  // nothing before the first entry
        Assert.Equal(new DateOnly(2026, 6, 20), Go(PeriodKind.Month, "2026-09-01", -1));

        Assert.Equal(new DateOnly(2026, 3, 10), Go(PeriodKind.Year, "2025-01-01", +1));
        Assert.Null(Go(PeriodKind.Year, "2026-05-05", +1));
        Assert.Equal(new DateOnly(2026, 3, 10), Go(PeriodKind.Week, "2026-06-20", -1));
        Assert.Equal(new DateOnly(2026, 6, 20), Go(PeriodKind.Day, "2026-03-10", +1));
        Assert.True(PeriodNavigation.CanMove(PeriodKind.Month, new DateOnly(2026, 8, 1), +1, c.Reader, today));
        Assert.Equal(new DateOnly(2026, 3, 10), c.Reader.LatestEntryDayBefore(new DateOnly(2026, 6, 20)));
        Assert.Equal(new DateOnly(2026, 6, 20), c.Reader.EarliestEntryDayAfter(new DateOnly(2026, 3, 10)));
    }

    [Fact]
    public void RemovingACategory_ThatIsAPayeesDefault_ArchivesIt()
    {
        var c = NewCore("a");
        var cat = c.Categories.Add("Czynsz", null, CategoryType.Expense);
        c.Payments.AddPayee("Wspólnota", "61 1090 1014 0000 0712 1981 2874", cat.Id);

        Assert.Equal(CategoryRemoval.Archived, c.Categories.Remove(cat.Id));
        Assert.True(c.Reader.GetCategories().First(x => x.Id == cat.Id).IsArchived);
    }

    [Fact]
    public void Move_ReordersSiblingsOnly()
    {
        var c = NewCore("a");
        var x = c.Categories.Add("X", null, CategoryType.Expense);
        var y = c.Categories.Add("Y", null, CategoryType.Expense);
        var z = c.Categories.Add("Z", null, CategoryType.Expense);

        c.Categories.Move(z.Id, -1);
        Assert.Equal(["X", "Z", "Y"], CategoryHelpers.Flatten(c.Reader.GetCategories()).Select(o => o.DisplayName));
        c.Categories.Move(x.Id, -1); // already first: no change
        Assert.Equal(["X", "Z", "Y"], CategoryHelpers.Flatten(c.Reader.GetCategories()).Select(o => o.DisplayName));
        c.Categories.Move(x.Id, +1);
        Assert.Equal(["Z", "X", "Y"], CategoryHelpers.Flatten(c.Reader.GetCategories()).Select(o => o.DisplayName));
    }
}

public class FolderInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-fi-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void MissingFolder_IsNotUsable() => Assert.False(FolderInspector.Inspect(_dir).CanUse);

    [Fact]
    public void EmptyFolder_IsUsable_WithoutExistingData()
    {
        Directory.CreateDirectory(_dir);
        var c = FolderInspector.Inspect(_dir);
        Assert.True(c.CanUse);
        Assert.False(c.HasExistingData);
    }

    [Fact]
    public void ExistingJournals_CloudOnlyFiles_AndConflictCopies_AreCounted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, $"journal_{TestEnv.DeviceA}.jsonl"), "");
        File.WriteAllText(Path.Combine(_dir, $"journal_{TestEnv.DeviceB}.jsonl"), "");
        File.WriteAllText(Path.Combine(_dir, $"journal_{TestEnv.DeviceB}-PC.jsonl"), "");

        var c = FolderInspector.Inspect(_dir, f => f.Name.Contains(TestEnv.DeviceB) && !f.Name.Contains("-PC"));
        Assert.Equal(2, c.JournalDevices.Count);
        Assert.Equal(1, c.CloudOnlyJournals);
        Assert.Equal(1, c.ConflictCopies);
        Assert.True(c.HasExistingData);
    }

    [Fact]
    public void OneDriveDetection_UsesPathSegments()
    {
        Assert.True(FolderInspector.IsInOneDrive(@"C:\Users\X\OneDrive - Firma\Budzet"));
        Assert.False(FolderInspector.IsInOneDrive(@"C:\Dane\Budzet"));
    }
}
