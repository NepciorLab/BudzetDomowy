using BudzetDomowy.Core.Backup;
using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Logging;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core;

/// <summary>Composition root of the Core library: one instance per running application (or test device).</summary>
public sealed class BudgetCore : IDisposable
{
    public const string DatabaseFileName = "budzet.db";

    public DeviceIdentity Device { get; }
    public string DataFolder { get; }
    public LocalDatabase Database { get; }
    public Materializer Materializer { get; }
    public LocalReader Reader { get; }
    public HybridLogicalClock Clock { get; }
    public SyncGuard Guard { get; }
    public EventRecorder Recorder { get; }
    public SyncService Sync { get; }
    public TransactionService Transactions { get; }
    public CategoryService Categories { get; }
    public CategoryRuleService Rules { get; }
    public EventService Events { get; }
    public ExchangeRateService Rates { get; }
    public BudgetService Budgets { get; }
    public RecurringService Recurring { get; }
    public ImportService Import { get; }

    /// <summary>Daily copies of the journals, kept next to the local database (outside the synchronised folder).</summary>
    public BackupService Backups { get; }

    private BudgetCore(DeviceIdentity device, string dataFolder, string localDataDir, LocalDatabase db, SyncOptions? syncOptions, IExchangeRateSource? rateSource)
    {
        Device = device;
        DataFolder = dataFolder;
        Database = db;
        Materializer = new Materializer(db);
        Reader = new LocalReader(db);
        Clock = new HybridLogicalClock(initial: Materializer.MaxHlc());
        Guard = new SyncGuard();
        var writer = new JournalWriter(dataFolder, device.DeviceId);
        Recorder = new EventRecorder(device, Clock, writer, Materializer, Guard);
        Sync = new SyncService(dataFolder, db, Materializer, Reader, Clock, Guard, syncOptions);
        Transactions = new TransactionService(Recorder, device, Reader);
        Categories = new CategoryService(Recorder, Reader);
        Rules = new CategoryRuleService(Reader, Recorder);
        Events = new EventService(Recorder, Reader, Transactions);
        Rates = new ExchangeRateService(Reader, rateSource ?? new NbpClient());
        Budgets = new BudgetService(Recorder, Reader);
        Recurring = new RecurringService(Recorder, Reader, Transactions, Rates);
        Import = new ImportService(Reader, Transactions, Categories);
        Backups = new BackupService(dataFolder, Path.Combine(localDataDir, "Backups"));
    }

    /// <summary>Puts the data back as it was in <paramref name="backup"/> and replays it into the database.</summary>
    public SyncStatus RestoreBackup(BackupInfo backup) => Sync.Rebuild(() => Backups.RestoreFiles(backup));

    /// <param name="localDataDir">Per-installation folder (outside OneDrive): device.json and the SQLite database.</param>
    /// <param name="dataFolder">The shared (OneDrive-synchronised) folder with the journals.</param>
    public static BudgetCore Create(string localDataDir, string dataFolder, string? deviceName = null, SyncOptions? syncOptions = null,
        IExchangeRateSource? rateSource = null, ILog? log = null)
    {
        if (log is not null)
        {
            var options = syncOptions ?? new SyncOptions();
            syncOptions = options.Log is null ? options.WithLog(log) : options;
        }
        var device = DeviceIdentity.LoadOrCreate(localDataDir, deviceName);
        var db = new LocalDatabase(Path.Combine(localDataDir, DatabaseFileName));
        return new BudgetCore(device, dataFolder, localDataDir, db, syncOptions, rateSource);
    }

    public void Dispose() => Sync.Dispose();
}
