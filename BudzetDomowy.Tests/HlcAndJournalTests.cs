using System.Text;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;

namespace BudzetDomowy.Tests;

public class HlcTests
{
    [Fact]
    public void Now_IsStrictlyIncreasing_EvenWhenWallClockStandsStillOrGoesBack()
    {
        var wall = new FakeClock();
        var clock = new HybridLogicalClock(wall.Now);
        var a = clock.Now();
        var b = clock.Now();
        wall.Ms -= 5_000;
        var c = clock.Now();
        wall.Ms += 60_000;
        var d = clock.Now();

        Assert.True(a < b && b < c && c < d);
        Assert.Equal(a.WallMs, c.WallMs); // went back: wall time frozen, counter advances
        Assert.Equal(wall.Ms, d.WallMs);
        Assert.Equal(0, d.Counter);
    }

    [Fact]
    public void Observe_PushesLocalEventsAfterRemoteOnes()
    {
        var wall = new FakeClock();
        var clock = new HybridLogicalClock(wall.Now);
        var remote = new HlcTimestamp(wall.Ms + 10_000, 7);
        clock.Observe(remote);
        Assert.True(clock.Now() > remote);
    }

    [Fact]
    public void TextForm_RoundTripsAndSortsLikeTheValue()
    {
        var values = new[] { new HlcTimestamp(5, 0), new HlcTimestamp(5, 12), new HlcTimestamp(1_800_000_000_000, 3), new HlcTimestamp(99, 99999) };
        foreach (var v in values) Assert.Equal(v, HlcTimestamp.Parse(v.ToString()));

        var byText = values.OrderBy(v => v.ToString(), StringComparer.Ordinal).ToArray();
        var byValue = values.Order().ToArray();
        Assert.Equal(byValue, byText);
    }
}

public class JournalFileTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static JournalEvent Ev(string device, long ms, int schema = JournalEvent.CurrentSchemaVersion) => new()
    {
        EventId = Guid.NewGuid(),
        DeviceId = device,
        Timestamp = new HlcTimestamp(ms, 0),
        EntityType = EntityTypes.Category,
        EntityId = Guid.NewGuid(),
        Operation = EventOperation.Upsert,
        Payload = JournalJson.ToPayload(new Category { Id = Guid.NewGuid(), Name = "N" }),
        SchemaVersion = schema,
    };

    [Fact]
    public void WriterAndReader_RoundTrip_AndReadingFromOffsetReturnsOnlyNewEvents()
    {
        var w = new JournalWriter(_env.Shared, TestEnv.DeviceA);
        var first = Ev(TestEnv.DeviceA, 1);
        var (_, end1) = w.Append(first);
        var second = Ev(TestEnv.DeviceA, 2);
        var third = Ev(TestEnv.DeviceA, 3);
        w.Append([second, third]);

        var all = JournalReader.ReadFrom(w.Path, 0);
        Assert.Equal([first.EventId, second.EventId, third.EventId], all.Events.Select(e => e.EventId));

        var tail = JournalReader.ReadFrom(w.Path, end1);
        Assert.Equal([second.EventId, third.EventId], tail.Events.Select(e => e.EventId));
        Assert.Equal(new FileInfo(w.Path).Length, tail.NewOffset);
    }

    [Fact]
    public void Reader_IgnoresIncompleteLastLine_UntilItIsCompleted()
    {
        var w = new JournalWriter(_env.Shared, TestEnv.DeviceA);
        w.Append(Ev(TestEnv.DeviceA, 1));
        var good = JournalReader.ReadFrom(w.Path, 0);

        var partial = JournalJson.Serialize(Ev(TestEnv.DeviceA, 2));
        var half = Encoding.UTF8.GetBytes(partial[..(partial.Length / 2)]);
        using (var fs = new FileStream(w.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) fs.Write(half);

        var r1 = JournalReader.ReadFrom(w.Path, good.NewOffset);
        Assert.Empty(r1.Events);
        Assert.Equal(good.NewOffset, r1.NewOffset); // nothing consumed, will be retried
        Assert.Equal(0, r1.CorruptLines);

        using (var fs = new FileStream(w.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(Encoding.UTF8.GetBytes(partial[(partial.Length / 2)..] + "\n"));

        var r2 = JournalReader.ReadFrom(w.Path, r1.NewOffset);
        Assert.Single(r2.Events);
    }

    [Fact]
    public void Reader_SkipsCorruptCompleteLines_AndKeepsReading()
    {
        var path = JournalFiles.PathFor(_env.Shared, TestEnv.DeviceA);
        var ev = Ev(TestEnv.DeviceA, 1);
        File.WriteAllText(path, "{ this is not json }\n\n" + JournalJson.Serialize(ev) + "\n{\"eventId\":\"nope\"}\n");

        var r = JournalReader.ReadFrom(path, 0);
        Assert.Single(r.Events);
        Assert.Equal(2, r.CorruptLines);
        Assert.Equal(new FileInfo(path).Length, r.NewOffset);
    }

    [Fact]
    public void Reader_StopsBeforeLineWithNewerSchema()
    {
        var path = JournalFiles.PathFor(_env.Shared, TestEnv.DeviceA);
        var ok = Ev(TestEnv.DeviceA, 1);
        var newer = Ev(TestEnv.DeviceA, 2, schema: JournalEvent.CurrentSchemaVersion + 1);
        var after = Ev(TestEnv.DeviceA, 3);
        var okLine = JournalJson.Serialize(ok) + "\n";
        File.WriteAllText(path, okLine + JournalJson.Serialize(newer) + "\n" + JournalJson.Serialize(after) + "\n");

        var r = JournalReader.ReadFrom(path, 0);
        Assert.Equal([ok.EventId], r.Events.Select(e => e.EventId));
        Assert.Equal(JournalEvent.CurrentSchemaVersion + 1, r.UnsupportedSchemaVersion);
        Assert.Equal(Encoding.UTF8.GetByteCount(okLine), r.NewOffset);
    }

    [Fact]
    public void Reader_HandlesBomAndCrLf()
    {
        var path = JournalFiles.PathFor(_env.Shared, TestEnv.DeviceA);
        var ev = Ev(TestEnv.DeviceA, 1);
        File.WriteAllText(path, JournalJson.Serialize(ev) + "\r\n", new UTF8Encoding(true));
        Assert.Single(JournalReader.ReadFrom(path, 0).Events);
    }

    [Fact]
    public void Writer_ClosesAnUnterminatedLineLeftByACrash_BeforeAppending()
    {
        var w = new JournalWriter(_env.Shared, TestEnv.DeviceA);
        w.Append(Ev(TestEnv.DeviceA, 1));
        using (var fs = new FileStream(w.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write("{\"eventId\":\"half"u8);

        var next = Ev(TestEnv.DeviceA, 2);
        w.Append(next);

        var r = JournalReader.ReadFrom(w.Path, 0);
        Assert.Equal(2, r.Events.Count);
        Assert.Equal(next.EventId, r.Events[1].EventId);
        Assert.Equal(1, r.CorruptLines);
    }

    [Fact]
    public void Reader_ReadsWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        var w = new JournalWriter(_env.Shared, TestEnv.DeviceA);
        w.Append(Ev(TestEnv.DeviceA, 1));
        using var open = new FileStream(w.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.Single(JournalReader.ReadFrom(w.Path, 0).Events);
    }

    [Fact]
    public void Reader_RestartsFromZeroWhenFileIsShorterThanStoredOffset()
    {
        var w = new JournalWriter(_env.Shared, TestEnv.DeviceA);
        w.Append(Ev(TestEnv.DeviceA, 1));
        var r = JournalReader.ReadFrom(w.Path, 10_000);
        Assert.True(r.FileShrank);
        Assert.Single(r.Events);
    }

    [Fact]
    public void FileNames_DistinguishRegularJournalsFromConflictCopies()
    {
        Assert.True(JournalFiles.TryGetDeviceId($"journal_{TestEnv.DeviceA}.jsonl", out var id));
        Assert.Equal(TestEnv.DeviceA, id);
        Assert.False(JournalFiles.TryGetDeviceId($"journal_{TestEnv.DeviceA}-DESKTOP-ABC.jsonl", out _));
        Assert.True(JournalFiles.LooksLikeConflictCopy($"journal_{TestEnv.DeviceA}-DESKTOP-ABC.jsonl"));
        Assert.True(JournalFiles.LooksLikeConflictCopy($"journal_{TestEnv.DeviceA} (1).jsonl"));
        Assert.False(JournalFiles.LooksLikeConflictCopy($"journal_{TestEnv.DeviceA}.jsonl"));
        Assert.False(JournalFiles.LooksLikeConflictCopy("notes.txt"));
    }

    [Fact]
    public void DeterministicId_IsStableAndDependsOnTemplateAndDate()
    {
        var t = Guid.NewGuid();
        var d = new DateOnly(2026, 5, 1);
        Assert.Equal(DeterministicId.ForRecurrence(t, d), DeterministicId.ForRecurrence(t, d));
        Assert.NotEqual(DeterministicId.ForRecurrence(t, d), DeterministicId.ForRecurrence(t, d.AddDays(1)));
        Assert.NotEqual(DeterministicId.ForRecurrence(t, d), DeterministicId.ForRecurrence(Guid.NewGuid(), d));
    }
}
