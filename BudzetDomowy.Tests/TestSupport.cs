using System.Text.Json;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Tests;

/// <summary>Controllable wall clock (milliseconds since epoch).</summary>
public sealed class FakeClock
{
    public long Ms { get; set; } = 1_800_000_000_000;
    public long Now() => Ms;
}

/// <summary>A shared "OneDrive" folder plus any number of simulated devices, each with its own local database.</summary>
public sealed class TestEnv : IDisposable
{
    public const string DeviceA = "00000000-0000-0000-0000-00000000000a";
    public const string DeviceB = "00000000-0000-0000-0000-00000000000b";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "budzet-tests-" + Guid.NewGuid().ToString("N"));
    public string Shared => Path.Combine(Root, "shared");
    private readonly List<Device> _devices = [];

    public TestEnv() => Directory.CreateDirectory(Shared);

    public Device NewDevice(string deviceId, FakeClock? clock = null, SyncOptions? options = null, string? dbName = null)
    {
        var d = new Device(this, deviceId, clock ?? new FakeClock(), options, dbName ?? "local-" + deviceId[^1]);
        _devices.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _devices) d.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public sealed class Device : IDisposable
{
    public DeviceIdentity Identity { get; }
    public FakeClock Wall { get; }
    public string DbPath { get; }
    public LocalDatabase Db { get; }
    public Materializer Materializer { get; }
    public LocalReader Reader { get; }
    public HybridLogicalClock Clock { get; }
    public SyncGuard Guard { get; } = new();
    public EventRecorder Recorder { get; }
    public SyncService Sync { get; }
    public string JournalPath { get; }

    public Device(TestEnv env, string deviceId, FakeClock wall, SyncOptions? options, string dbName)
    {
        Identity = new DeviceIdentity(deviceId, "PC-" + deviceId[^1]);
        Wall = wall;
        DbPath = Path.Combine(env.Root, dbName, "budzet.db");
        Db = new LocalDatabase(DbPath);
        Materializer = new Materializer(Db);
        Reader = new LocalReader(Db);
        Clock = new HybridLogicalClock(wall.Now, Materializer.MaxHlc());
        var writer = new JournalWriter(env.Shared, deviceId);
        JournalPath = writer.Path;
        Recorder = new EventRecorder(Identity, Clock, writer, Materializer, Guard);
        Sync = new SyncService(env.Shared, Db, Materializer, Reader, Clock, Guard, options);
    }

    public void Dispose() => Sync.Dispose();

    public Transaction NewTransaction(decimal amount = 10m, string note = "x", Guid? category = null) => new()
    {
        Id = Guid.NewGuid(),
        Date = new DateOnly(2026, 3, 15),
        Type = TransactionType.Expense,
        Amount = amount,
        AmountPln = amount,
        CategoryId = category,
        Note = note,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Wall.Ms),
        CreatedByDeviceId = Identity.DeviceId,
    };

    /// <summary>Everything the device knows, as comparable JSON.</summary>
    public string Snapshot() => JsonSerializer.Serialize(new
    {
        t = Reader.GetTransactions(),
        c = Reader.GetCategories(),
        e = Reader.GetSpecialEvents(),
        b = Reader.GetBudgets(),
        r = Reader.GetRecurringTransactions(),
        k = Reader.GetCategoryRules(),
    }, JournalJson.Options);
}
