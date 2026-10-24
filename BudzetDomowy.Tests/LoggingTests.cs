using BudzetDomowy.Core;
using BudzetDomowy.Core.Logging;

namespace BudzetDomowy.Tests;

public sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warn(string message) => Lines.Add("WARN " + message);
    public void Error(string message, Exception? exception = null) => Lines.Add("ERROR " + message);
}

public class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void WritesOneFilePerDay_WithLevelMessageAndTheWholeExceptionChain()
    {
        var now = new DateTime(2026, 10, 24, 18, 30, 5);
        var log = new FileLog(_dir, () => now);

        log.Info("started");
        log.Error("failed to save", new InvalidOperationException("outer", new IOException("inner")));
        now = now.AddDays(1);
        log.Warn("next day");

        var first = File.ReadAllText(Path.Combine(_dir, "budzet-2026-10-24.log"));
        Assert.Contains("2026-10-24 18:30:05.000 INFO  started", first);
        Assert.Contains("ERROR failed to save", first);
        Assert.Contains("System.InvalidOperationException: outer", first);
        Assert.Contains("System.IO.IOException: inner", first);
        Assert.Contains("WARN  next day", File.ReadAllText(Path.Combine(_dir, "budzet-2026-10-25.log")));
    }

    [Fact]
    public void OldFilesAreRemoved_RecentOnesAndOtherFilesStay()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "budzet-2026-09-01.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "budzet-2026-10-20.log"), "recent");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "mine");

        new FileLog(_dir, () => new DateTime(2026, 10, 24), keepDays: 14).Info("x");

        Assert.False(File.Exists(Path.Combine(_dir, "budzet-2026-09-01.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "budzet-2026-10-20.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }

    [Fact]
    public void ALogThatCannotBeWritten_NeverThrows()
    {
        var blocker = Path.Combine(_dir, "file-not-folder");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "x");
        var log = new FileLog(Path.Combine(blocker, "logs")); // a folder cannot be created under a file
        log.Error("still fine", new Exception("boom"));
        Assert.True(true);
    }

    [Fact]
    public void SyncProblems_AreLoggedWhenTheyAppearAndWhenTheyEnd_NotOnEveryPass()
    {
        var root = Path.Combine(_dir, "sync");
        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared);
        var log = new ListLog();
        using var core = BudgetCore.Create(Path.Combine(root, "l"), shared, "x", rateSource: new FakeRateSource(), log: log);

        // a conflict copy next to the journals is a problem the user should hear about
        var conflict = Path.Combine(shared, $"journal_{TestEnv.DeviceB}-LAPTOP.jsonl");
        File.WriteAllText(conflict, "");
        core.Sync.SyncOnce();
        core.Sync.SyncOnce();
        core.Sync.SyncOnce();
        Assert.Single(log.Lines, l => l.StartsWith("WARN Sync issue ConflictCopy"));

        File.Delete(conflict);
        core.Sync.SyncOnce();
        Assert.Contains(log.Lines, l => l.Contains("resolved"));
    }
}
