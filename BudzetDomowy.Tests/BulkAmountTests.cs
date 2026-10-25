using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Tests;

/// <summary>Changing the amount of many transactions at once.</summary>
public sealed class BulkAmountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-bulk-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ChangeAmount_GivesAllTheSameAmount_KeepsTheRest_AndReturnsWhatWasThere()
    {
        var core = NewCore();
        var cat = core.Reader.GetCategories().First().Id;
        var a = core.Transactions.Add(Day, TransactionType.Expense, 10m, cat, "a", null, place: "A");
        var b = core.Transactions.Add(Day.AddDays(1), TransactionType.Income, 20m, null, "b", null, place: "B");
        var untouched = core.Transactions.Add(Day, TransactionType.Expense, 30m, null, "c", null);

        var before = core.Transactions.ChangeAmount([a.Id, b.Id], 99.99m);

        Assert.Equal([10m, 20m], before.OrderBy(t => t.Amount).Select(t => t.Amount));
        var all = core.Reader.GetTransactions().ToDictionary(t => t.Id);
        Assert.All(new[] { a.Id, b.Id }, id =>
        {
            Assert.Equal(99.99m, all[id].Amount);
            Assert.Equal(99.99m, all[id].AmountPln);
            Assert.Equal(99.99m, Assert.Single(all[id].Lines).Amount);                      // the line follows the amount
        });
        Assert.Equal((cat, "A", TransactionType.Expense, Day), (all[a.Id].CategoryId, all[a.Id].Place, all[a.Id].Type, all[a.Id].Date));
        Assert.Equal(TransactionType.Income, all[b.Id].Type);
        Assert.Equal(30m, all[untouched.Id].Amount);

        core.Transactions.Restore(before);                                                  // undo
        var restored = core.Reader.GetTransactions().ToDictionary(t => t.Id);
        Assert.Equal((10m, 20m), (restored[a.Id].Amount, restored[b.Id].Amount));
    }

    [Fact]
    public void ChangeAmount_OfAForeignEntry_IsInItsOwnCurrency_AndZlotyFollowTheRate()
    {
        var core = NewCore();
        var eur = core.Transactions.Add(Day, TransactionType.Expense, 10m, null, "euro", null, currency: "EUR", rateToPln: 4.25m);
        var pln = core.Transactions.Add(Day, TransactionType.Expense, 10m, null, "zloty", null);

        core.Transactions.ChangeAmount([eur.Id, pln.Id], 20m);

        var all = core.Reader.GetTransactions().ToDictionary(t => t.Id);
        Assert.Equal((20m, "EUR", 85m), (all[eur.Id].Amount, all[eur.Id].CurrencyCode, all[eur.Id].AmountPln));
        Assert.Equal((20m, 20m), (all[pln.Id].Amount, all[pln.Id].AmountPln));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ChangeAmount_RefusesAnAmountThatIsNotPositive(int amount)
    {
        var core = NewCore();
        var t = core.Transactions.Add(Day, TransactionType.Expense, 10m, null, "a", null);
        Assert.Throws<ArgumentOutOfRangeException>(() => core.Transactions.ChangeAmount([t.Id], amount));
        Assert.Equal(10m, core.Reader.GetTransactions().Single().Amount);
    }
}
