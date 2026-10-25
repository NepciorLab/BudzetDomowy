using System.IO.Compression;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Backup;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;

namespace BudzetDomowy.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-backup-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Backups => Path.Combine(_root, "backups");
    private DateTime _now = new(2026, 10, 25, 3, 15, 0);
    private readonly List<BudgetCore> _cores = [];

    private const string DeviceA = "00000000-0000-0000-0000-00000000000a";
    private const string DeviceB = "00000000-0000-0000-0000-00000000000b";

    public BackupTests() => Directory.CreateDirectory(Data);

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BackupService Service(int keep = 10) => new(Data, Backups, () => _now, keep);

    private string Journal(string device) => JournalFiles.PathFor(Data, device);

    private void WriteJournal(string device, string content) => File.WriteAllText(Journal(device), content);

    [Fact]
    public void Create_ZipsTheJournalsOnly_AndListsTheCopy()
    {
        WriteJournal(DeviceA, "a1\na2\n");
        WriteJournal(DeviceB, "b1\n");
        File.WriteAllText(Path.Combine(Data, "journal_" + DeviceA + "-DESKTOP (1).jsonl"), "conflict copy");   // not data
        File.WriteAllText(Path.Combine(Data, "other.txt"), "x");

        var backup = Service().Create(BackupKind.Manual)!;

        Assert.Equal(2, backup.JournalFiles);
        Assert.Equal(BackupKind.Manual, backup.Kind);
        Assert.Equal(_now, backup.CreatedLocal);
        Assert.EndsWith("_manual.zip", backup.FileName);
        using var zip = ZipFile.OpenRead(backup.Path);
        Assert.Equal(["backup.json", JournalFiles.FileNameFor(DeviceA), JournalFiles.FileNameFor(DeviceB)], zip.Entries.Select(e => e.Name).Order());
        Assert.Equal(backup.Path, Assert.Single(Service().List()).Path);
        Assert.Empty(Directory.GetFiles(Backups, "*.tmp"));
    }

    [Fact]
    public void Create_WithNothingToCopy_MakesNoFile()
    {
        Assert.Null(Service().Create(BackupKind.Automatic));
        Assert.Null(Service().EnsureDaily());
        Assert.Empty(Service().List());
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void EnsureDaily_MakesOneCopyPerDay_AndANewOneOnTheNextDay()
    {
        WriteJournal(DeviceA, "a1\n");
        var service = Service();

        Assert.NotNull(service.EnsureDaily());
        _now = _now.AddHours(5);
        Assert.Null(service.EnsureDaily());                       // later the same day: nothing
        Assert.Single(service.List());

        _now = _now.AddDays(1);
        var next = service.EnsureDaily();
        Assert.NotNull(next);
        Assert.Equal(BackupKind.Automatic, next!.Kind);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void EnsureDaily_AManualCopyOfTodayCounts_ButASafetyCopyDoesNot()
    {
        WriteJournal(DeviceA, "a1\n");
        var service = Service();
        service.Create(BackupKind.Manual);
        Assert.Null(service.EnsureDaily());

        var other = new BackupService(Data, Path.Combine(_root, "other"), () => _now);
        var restoreSource = other.Create(BackupKind.Manual)!;
        other.RestoreFiles(restoreSource);                         // leaves a "before restore" copy
        Assert.Contains(other.List(), b => b.Kind == BackupKind.BeforeRestore);
    }

    [Fact]
    public void OnlyTheNewestTenCopiesAreKept()
    {
        WriteJournal(DeviceA, "a1\n");
        var service = Service();
        for (var day = 0; day < 13; day++)
        {
            _now = new DateTime(2026, 10, 1, 4, 0, 0).AddDays(day);
            service.EnsureDaily();
        }

        var kept = service.List();
        Assert.Equal(10, kept.Count);
        Assert.Equal(new DateTime(2026, 10, 13, 4, 0, 0), kept[0].CreatedLocal);               // newest first
        Assert.Equal(new DateTime(2026, 10, 4, 4, 0, 0), kept[^1].CreatedLocal);               // the three oldest are gone
    }

    [Fact]
    public void Restore_PutsBackTheOldJournals_RemovesNewerOnes_AndKeepsASafetyCopy()
    {
        WriteJournal(DeviceA, "old a\n");
        var service = Service();
        var backup = service.Create(BackupKind.Manual)!;

        _now = _now.AddHours(2);
        WriteJournal(DeviceA, "old a\nnew a\n");
        WriteJournal(DeviceB, "only after the backup\n");

        service.RestoreFiles(backup);

        Assert.Equal("old a\n", File.ReadAllText(Journal(DeviceA)));
        Assert.False(File.Exists(Journal(DeviceB)));                // not part of that state
        var safety = Assert.Single(service.List(), b => b.Kind == BackupKind.BeforeRestore);
        Assert.Equal(2, safety.JournalFiles);

        // the safety copy is a way back
        service.RestoreFiles(safety);
        Assert.Equal("old a\nnew a\n", File.ReadAllText(Journal(DeviceA)));
        Assert.Equal("only after the backup\n", File.ReadAllText(Journal(DeviceB)));
    }

    [Fact]
    public void Restore_OfTheOldestCopy_SurvivesTheCleanUp()
    {
        WriteJournal(DeviceA, "state 0\n");
        var service = Service(keep: 3);
        BackupInfo? oldest = null;
        for (var day = 0; day < 3; day++)
        {
            _now = new DateTime(2026, 10, 1, 4, 0, 0).AddDays(day);
            WriteJournal(DeviceA, $"state {day}\n");
            var b = service.Create(BackupKind.Automatic)!;
            oldest ??= b;
        }

        _now = _now.AddDays(1);
        service.RestoreFiles(oldest!);                               // makes a fourth copy: the oldest must not be the one dropped

        Assert.Equal("state 0\n", File.ReadAllText(Journal(DeviceA)));
        Assert.Contains(service.List(), b => b.Path == oldest!.Path);
    }

    [Fact]
    public void Restore_OfAnUnusableCopy_ChangesNothing()
    {
        WriteJournal(DeviceA, "current\n");
        Directory.CreateDirectory(Backups);
        var empty = Path.Combine(Backups, "backup_2026-10-20_040000_auto.zip");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        var info = new BackupInfo(empty, new DateTime(2026, 10, 20, 4, 0, 0), BackupKind.Automatic, new FileInfo(empty).Length, 0);

        Assert.Throws<InvalidDataException>(() => Service().RestoreFiles(info));
        Assert.Equal("current\n", File.ReadAllText(Journal(DeviceA)));
        Assert.Empty(Service().List().Where(b => b.Kind == BackupKind.BeforeRestore));

        Assert.Throws<FileNotFoundException>(() => Service().RestoreFiles(info with { Path = Path.Combine(Backups, "missing.zip") }));
    }

    [Fact]
    public void List_IgnoresFilesThatAreNotCopies()
    {
        WriteJournal(DeviceA, "a\n");
        var service = Service();
        service.Create(BackupKind.Manual);
        File.WriteAllText(Path.Combine(Backups, "backup_garbage.zip"), "not a zip");
        File.WriteAllText(Path.Combine(Backups, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(Backups, "backup_2026-10-20_040000_auto.zip"), "damaged zip");

        Assert.Single(service.List());
    }

    [Fact]
    public void RestoringACopyBringsBackTheTransactions_InTheDatabaseToo()
    {
        var local = Path.Combine(_root, "local");
        var core = BudgetCore.Create(local, Data, "Test");
        _cores.Add(core);
        core.Categories.SeedIfEmpty();
        var category = core.Reader.GetCategories().First(c => c.Name == "Spożywcze");
        core.Transactions.Add(new DateOnly(2026, 10, 1), TransactionType.Expense, 10m, category.Id, "", null);
        core.Transactions.Add(new DateOnly(2026, 10, 2), TransactionType.Expense, 20m, category.Id, "", null);

        var backup = core.Backups.Create(BackupKind.Manual)!;
        Assert.StartsWith(Path.Combine(Data, "Backups"), backup.Path);                  // in the shared data folder, so every computer sees it

        core.Transactions.Add(new DateOnly(2026, 10, 3), TransactionType.Expense, 30m, category.Id, "", null);
        Assert.Equal(3, core.Reader.GetTransactions().Count);

        core.RestoreBackup(backup);

        Assert.Equal([10m, 20m], core.Reader.GetTransactions().OrderBy(t => t.Date).Select(t => t.AmountPln));

        // and the program keeps working: new entries are written and read as usual
        core.Transactions.Add(new DateOnly(2026, 10, 4), TransactionType.Income, 5m, null, "", null);
        Assert.Equal(3, core.Reader.GetTransactions().Count);

        // the state from before the restore is still available
        var safety = Assert.Single(core.Backups.List(), b => b.Kind == BackupKind.BeforeRestore);
        core.RestoreBackup(safety);
        Assert.Equal(3, core.Reader.GetTransactions().Count);
        Assert.Contains(core.Reader.GetTransactions(), t => t.AmountPln == 30m);
    }

    [Fact]
    public void ASecondComputerSeesTheSameCopies_AndDoesNotMakeAnotherOneToday()
    {
        var a = BudgetCore.Create(Path.Combine(_root, "local-a"), Data, "A");
        var b = BudgetCore.Create(Path.Combine(_root, "local-b"), Data, "B");
        _cores.Add(a);
        _cores.Add(b);
        a.Categories.SeedIfEmpty();

        Assert.NotNull(a.Backups.EnsureDaily());
        Assert.Equal(a.Backups.BackupFolder, b.Backups.BackupFolder);
        Assert.Equal(Path.Combine(Data, "Backups"), b.Backups.BackupFolder);
        Assert.Single(b.Backups.List());
        Assert.Null(b.Backups.EnsureDaily());                     // A already made today's copy
    }

    [Fact]
    public void CopiesOfAnEarlierVersion_AreMovedFromTheLocalFolderToTheSharedOne()
    {
        WriteJournal(DeviceA, "a\n");
        var local = Path.Combine(_root, "local-old", "Backups");
        Directory.CreateDirectory(local);
        var old = new BackupService(Data, local, () => new DateTime(2026, 10, 20, 4, 0, 0)).Create(BackupKind.Automatic)!;
        var shared = Service();
        shared.Create(BackupKind.Manual);

        Assert.Equal(1, shared.AdoptFrom(local));
        Assert.False(File.Exists(old.Path));
        Assert.Equal(2, shared.List().Count);
        Assert.Equal(0, shared.AdoptFrom(local));                  // nothing left to move
        Assert.Equal(0, shared.AdoptFrom(Backups));                // the same folder: nothing to do
        Assert.Equal(0, shared.AdoptFrom(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void CreatingTheCoreMovesLocalCopiesOfVersion040()
    {
        var localDir = Path.Combine(_root, "local-core");
        var oldFolder = Path.Combine(localDir, "Backups");
        Directory.CreateDirectory(oldFolder);
        WriteJournal(DeviceA, "a\n");
        new BackupService(Data, oldFolder, () => new DateTime(2026, 10, 20, 4, 0, 0)).Create(BackupKind.Automatic);

        var core = BudgetCore.Create(localDir, Data, "Test");
        _cores.Add(core);

        Assert.Empty(Directory.GetFiles(oldFolder, "*.zip"));
        Assert.Contains(core.Backups.List(), b => b.CreatedLocal == new DateTime(2026, 10, 20, 4, 0, 0));
    }
}
