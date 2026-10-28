using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

/// <summary>Finding an entry that is already there, and joining a new one to it.</summary>
public sealed class DuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-dup-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        var c = BudgetCore.Create(Path.Combine(_root, "l"), Path.Combine(_root, "shared"), "x", rateSource: new FakeRateSource());
        _cores.Add(c);
        c.Categories.SeedIfEmpty();
        return c;
    }

    private static readonly DateOnly Day = new(2026, 3, 1);

    private static IReadOnlyList<(Guid? CategoryId, decimal Amount)> Lines(params (Guid? Category, decimal Amount)[] lines) => lines;

    [Fact]
    public void Find_NeedsTheSameDay_Place_Category_Amount_Kind_AndCurrency()
    {
        var core = NewCore();
        var cats = core.Reader.GetCategories();
        var food = cats[0].Id;
        var car = cats[1].Id;
        var existing = core.Transactions.Add(Day, TransactionType.Expense, 25m, food, "n", null, place: "Lidl");
        var all = core.Reader.GetTransactions();

        Assert.Equal(existing.Id, DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Lidl", Lines((food, 25m))).Single().Existing.Id);
        Assert.Single(DuplicateFinder.Find(all, Day, TransactionType.Expense, "pln", "  lidl ", Lines((food, 25m))));       // case and spaces do not matter

        Assert.Empty(DuplicateFinder.Find(all, Day.AddDays(1), TransactionType.Expense, "PLN", "Lidl", Lines((food, 25m))));  // another day
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Biedronka", Lines((food, 25m))));       // another place
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Lidl", Lines((car, 25m))));             // another category
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Lidl", Lines((food, 25.01m))));         // another amount
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Income, "PLN", "Lidl", Lines((food, 25m))));             // an income is no expense
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "EUR", "Lidl", Lines((food, 25m))));            // another currency
    }

    [Fact]
    public void Find_WithoutAPlace_MatchesEntriesWithoutAPlace()
    {
        var core = NewCore();
        var food = core.Reader.GetCategories()[0].Id;
        core.Transactions.Add(Day, TransactionType.Expense, 10m, food, "", null);
        var all = core.Reader.GetTransactions();
        Assert.Single(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", null, Lines((food, 10m))));
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Lidl", Lines((food, 10m))));
    }

    [Fact]
    public void Find_ComparesEveryLineOfTheNewEntry_AndTellsWhichOnesHaveATwin()
    {
        var core = NewCore();
        var cats = core.Reader.GetCategories();
        var (a, b, c) = (cats[0].Id, cats[1].Id, cats[2].Id);
        core.Transactions.Add(Day, TransactionType.Expense, 60m, a, "n", null, place: "Sklep");
        core.Transactions.Add(Day, TransactionType.Expense, 40m, b, "n", null, place: "Sklep");
        var all = core.Reader.GetTransactions();

        var found = DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Sklep", Lines((a, 60m), (c, 15m), (b, 40m)));
        Assert.Equal([0, 2], found.Select(m => m.LineIndex));                           // the middle line is new
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Sklep", Lines((a, 61m), (c, 15m))));
    }

    [Fact]
    public void Find_AlsoLooksIntoTheLinesOfAnEntryThatHasSeveral()
    {
        var core = NewCore();
        var cats = core.Reader.GetCategories();
        var (a, b) = (cats[0].Id, cats[1].Id);
        var many = core.Transactions.Add(Day, TransactionType.Expense, 100m, a, "n", null, place: "Sklep") with
        {
            Lines = [new TransactionLine { Id = Guid.NewGuid(), CategoryId = a, Amount = 70m }, new TransactionLine { Id = Guid.NewGuid(), CategoryId = b, Amount = 30m }],
        };
        core.Transactions.Update(many);
        var all = core.Reader.GetTransactions();

        Assert.Single(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Sklep", Lines((b, 30m))));              // one of its lines
        Assert.Empty(DuplicateFinder.Find(all, Day, TransactionType.Expense, "PLN", "Sklep", Lines((b, 100m))));            // the total is not a line of it
    }

    [Fact]
    public void MergeInto_AddsTheAmountToTheEntry_KeepsTheRest_AndCanBeUndone()
    {
        var core = NewCore();
        var food = core.Reader.GetCategories()[0].Id;
        var existing = core.Transactions.Add(Day, TransactionType.Expense, 25m, food, "n", null, place: "Lidl");

        var joined = core.Transactions.MergeInto(existing, food, 25m);

        Assert.Equal(50m, joined.Amount);
        Assert.Equal(50m, joined.AmountPln);
        Assert.Equal(50m, Assert.Single(joined.Lines).Amount);                          // the line follows
        var stored = core.Reader.GetTransactions().Single(t => t.Id == existing.Id);
        Assert.Equal((50m, "Lidl", food, Day), (stored.Amount, stored.Place, stored.CategoryId, stored.Date));
        Assert.Single(core.Reader.GetTransactions().Where(t => t.Place == "Lidl"));      // no second entry

        core.Transactions.Restore([existing]);
        Assert.Equal(25m, core.Reader.GetTransactions().Single(t => t.Id == existing.Id).Amount);
    }

    [Fact]
    public void MergeInto_AnEntryWithSeveralLines_GrowsInTheLineOfThatCategory()
    {
        var core = NewCore();
        var cats = core.Reader.GetCategories();
        var (a, b) = (cats[0].Id, cats[1].Id);
        var many = core.Transactions.Add(Day, TransactionType.Expense, 100m, a, "n", null, place: "Sklep") with
        {
            Lines = [new TransactionLine { Id = Guid.NewGuid(), CategoryId = a, Amount = 70m }, new TransactionLine { Id = Guid.NewGuid(), CategoryId = b, Amount = 30m }],
        };
        core.Transactions.Update(many);
        many = core.Reader.GetTransactions().Single(t => t.Id == many.Id);

        var joined = core.Transactions.MergeInto(many, b, 30m);

        Assert.Equal(130m, joined.Amount);
        Assert.Equal([70m, 60m], joined.Lines.Select(l => l.Amount));                    // the line of the second category doubled
        Assert.Equal(130m, joined.Lines.Sum(l => l.Amount));                              // the lines still add up to the entry
    }

    [Fact]
    public void MergeInto_ForeignEntry_GrowsInItsOwnCurrency_AndZlotyFollowTheRate()
    {
        var core = NewCore();
        var food = core.Reader.GetCategories()[0].Id;
        var existing = core.Transactions.Add(Day, TransactionType.Expense, 10m, food, "n", null, place: "Shop", currency: "EUR", rateToPln: 4.5m);
        var joined = core.Transactions.MergeInto(existing, food, 10m);
        Assert.Equal((20m, 90m), (joined.Amount, joined.AmountPln));
    }
}
