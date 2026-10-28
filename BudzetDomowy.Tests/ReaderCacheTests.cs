using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Tests;

/// <summary>The transactions are read from the database once and kept until the data changes.</summary>
public sealed class ReaderCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-cache-" + Guid.NewGuid().ToString("N"));
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
    public void WithoutAChange_TheSameListIsReturned_AndEveryChangeIsSeenAtOnce()
    {
        var core = NewCore();
        var a = core.Transactions.Add(Day, TransactionType.Expense, 10m, null, "a", null, place: "A");

        var first = core.Reader.GetTransactions();
        Assert.Same(first, core.Reader.GetTransactions());                                  // read once

        var b = core.Transactions.Add(Day.AddDays(1), TransactionType.Expense, 20m, null, "b", null, place: "B");
        var second = core.Reader.GetTransactions();
        Assert.NotSame(first, second);
        Assert.Equal([a.Id, b.Id], second.Select(t => t.Id));                               // the new one is there, in the order of the day

        core.Transactions.Update(a with { Amount = 11m });
        Assert.Equal(11m, core.Reader.GetTransactions().Single(t => t.Id == a.Id).Amount);  // an edit too

        core.Transactions.Delete([b]);
        Assert.Equal([a.Id], core.Reader.GetTransactions().Select(t => t.Id));              // and a delete

        core.Database.ClearMaterializedData();
        Assert.Empty(core.Reader.GetTransactions());                                         // and a rebuild from the journals
    }

    [Fact]
    public void ARangeOfDays_GivesWhatTheWholeListHasInIt()
    {
        var core = NewCore();
        foreach (var d in new[] { 1, 5, 9, 15, 20 }) core.Transactions.Add(Day.AddDays(d), TransactionType.Expense, d, null, "", null);

        Assert.Equal([5m, 9m, 15m], core.Reader.GetTransactions(Day.AddDays(5), Day.AddDays(15)).Select(t => t.Amount));
        Assert.Equal([15m, 20m], core.Reader.GetTransactions(from: Day.AddDays(10)).Select(t => t.Amount));
        Assert.Equal([1m, 5m], core.Reader.GetTransactions(to: Day.AddDays(5)).Select(t => t.Amount));
        Assert.Empty(core.Reader.GetTransactions(Day.AddDays(30), Day.AddDays(40)));
        Assert.Equal(5, core.Reader.GetTransactions().Count);
    }

    [Fact]
    public void ChangesFromAnotherDeviceAreSeenAfterTheSynchronisation()
    {
        var core = NewCore();
        core.Transactions.Add(Day, TransactionType.Expense, 10m, null, "a", null);
        var before = core.Reader.GetTransactions();

        // a second device writes its own journal into the shared folder
        var other = BudgetCore.Create(Path.Combine(_root, "l2"), Path.Combine(_root, "shared"), "y", rateSource: new FakeRateSource());
        _cores.Add(other);
        other.Sync.SyncOnce();
        other.Transactions.Add(Day.AddDays(2), TransactionType.Expense, 33m, null, "b", null);

        core.Sync.SyncOnce();
        var after = core.Reader.GetTransactions();
        Assert.NotSame(before, after);
        Assert.Contains(after, t => t.Amount == 33m);
    }
}
