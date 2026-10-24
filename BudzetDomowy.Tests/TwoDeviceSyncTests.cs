using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Tests;

public class TwoDeviceSyncTests : IDisposable
{
    private readonly TestEnv _env = new();
    private readonly FakeClock _wall = new();

    public void Dispose() => _env.Dispose();

    private (Device A, Device B) Pair(bool sharedClock = true)
    {
        var a = _env.NewDevice(TestEnv.DeviceA, sharedClock ? _wall : new FakeClock());
        var b = _env.NewDevice(TestEnv.DeviceB, sharedClock ? _wall : new FakeClock());
        return (a, b);
    }

    private static void SyncBoth(Device a, Device b)
    {
        a.Sync.SyncOnce();
        b.Sync.SyncOnce();
        a.Sync.SyncOnce();
    }

    [Fact]
    public void ParallelAdds_OnBothDevices_ConvergeToTheUnion()
    {
        var (a, b) = Pair();
        var ta = a.NewTransaction(10, "from A");
        var tb = b.NewTransaction(20, "from B");

        a.Recorder.Save(ta);
        b.Recorder.Save(tb); // neither has synced yet
        SyncBoth(a, b);

        Assert.Equal(2, a.Reader.GetTransactions().Count);
        Assert.Equal(2, b.Reader.GetTransactions().Count);
        Assert.Equal(a.Snapshot(), b.Snapshot());
        Assert.Equal(10m, b.Reader.GetTransaction(ta.Id)!.Amount);
    }

    [Fact]
    public void EditOfTheSameEntity_LaterTimestampWinsOnBothDevices()
    {
        var (a, b) = Pair();
        var t = a.NewTransaction(10, "orig");
        a.Recorder.Save(t);
        SyncBoth(a, b);

        _wall.Ms += 1000;
        a.Recorder.Save(t with { Note = "edit A", Amount = 11m, AmountPln = 11m });
        _wall.Ms += 1000;
        b.Recorder.Save(t with { Note = "edit B", Amount = 12m, AmountPln = 12m }); // later, B never saw A's edit
        SyncBoth(a, b);

        Assert.Equal("edit B", a.Reader.GetTransaction(t.Id)!.Note);
        Assert.Equal("edit B", b.Reader.GetTransaction(t.Id)!.Note);
        Assert.Equal(a.Snapshot(), b.Snapshot());
    }

    [Fact]
    public void EditOfTheSameEntity_WithIdenticalTimestamps_DeviceIdBreaksTheTie()
    {
        var (a, b) = Pair(); // same fake wall clock => equal HLC, counter 0 on both
        var id = Guid.NewGuid();
        var baseTx = a.NewTransaction(1, "base") with { Id = id };

        var ea = a.Recorder.Save(baseTx with { Note = "A" });
        var eb = b.Recorder.Save(baseTx with { Note = "B" });
        Assert.Equal(ea.Timestamp, eb.Timestamp);

        SyncBoth(a, b);
        Assert.Equal("B", a.Reader.GetTransaction(id)!.Note); // "…0b" > "…0a"
        Assert.Equal("B", b.Reader.GetTransaction(id)!.Note);
    }

    [Fact]
    public void LaterEditWinsOverEarlierDelete_AndLaterDeleteWinsOverEarlierEdit()
    {
        var (a, b) = Pair();
        var keep = a.NewTransaction(1, "will be edited after delete");
        var gone = a.NewTransaction(2, "will be deleted after edit");
        a.Recorder.Save(keep);
        a.Recorder.Save(gone);
        SyncBoth(a, b);

        _wall.Ms += 1000;
        a.Recorder.Delete(EntityTypes.Transaction, keep.Id);
        b.Recorder.Save(gone with { Note = "edited" });
        _wall.Ms += 1000;
        b.Recorder.Save(keep with { Note = "resurrected on purpose" }); // newer than A's delete
        a.Recorder.Delete(EntityTypes.Transaction, gone.Id);              // newer than B's edit
        SyncBoth(a, b);

        foreach (var d in new[] { a, b })
        {
            Assert.Equal("resurrected on purpose", d.Reader.GetTransaction(keep.Id)!.Note);
            Assert.Null(d.Reader.GetTransaction(gone.Id));
            Assert.True(d.Reader.IsTombstone(EntityTypes.Transaction, gone.Id));
        }
        Assert.Equal(a.Snapshot(), b.Snapshot());
    }

    [Fact]
    public void Tombstone_BlocksAnOlderEditThatArrivesLate()
    {
        var (a, b) = Pair();
        var t = a.NewTransaction(5, "x");
        a.Recorder.Save(t);
        SyncBoth(a, b);

        _wall.Ms += 1000;
        b.Recorder.Save(t with { Note = "old edit" });   // older timestamp, not yet visible to A
        _wall.Ms += 1000;
        a.Recorder.Delete(EntityTypes.Transaction, t.Id); // newer
        a.Sync.SyncOnce(); // A now reads B's old edit after having deleted
        b.Sync.SyncOnce();

        Assert.Null(a.Reader.GetTransaction(t.Id));
        Assert.Null(b.Reader.GetTransaction(t.Id));
        Assert.True(a.Reader.IsTombstone(EntityTypes.Transaction, t.Id));
    }

    [Fact]
    public void ReadingTheSameEventsAgain_ChangesNothing()
    {
        var (a, b) = Pair();
        for (var i = 0; i < 5; i++) a.Recorder.Save(a.NewTransaction(i + 1, "n" + i));
        SyncBoth(a, b);
        var before = b.Snapshot();

        // Forget the offsets: B re-reads A's whole journal.
        using (var conn = b.Db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM journal_offsets";
            cmd.ExecuteNonQuery();
        }
        var status = b.Sync.SyncOnce();

        Assert.Equal(0, status.LastApplied.Applied);
        Assert.True(status.LastApplied.Ignored >= 5);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void ApplyingTheSameBatchTwice_IsIdempotent()
    {
        var a = _env.NewDevice(TestEnv.DeviceA, _wall);
        var t = a.NewTransaction(3, "once");
        var ev = new JournalEvent
        {
            EventId = Guid.NewGuid(), DeviceId = TestEnv.DeviceB, Timestamp = new HlcTimestamp(_wall.Ms, 0),
            EntityType = EntityTypes.Transaction, EntityId = t.Id, Operation = EventOperation.Upsert,
            Payload = JournalJson.ToPayload(t),
        };
        var r1 = a.Materializer.Apply([ev]);
        var snap = a.Snapshot();
        var r2 = a.Materializer.Apply([ev]);

        Assert.Equal(1, r1.Applied);
        Assert.Equal(0, r2.Applied);
        Assert.Equal(1, r2.Ignored);
        Assert.Equal(snap, a.Snapshot());
    }

    [Fact]
    public void OwnEvents_AreNotReadAgainBySync_AndOffsetTracksTheFile()
    {
        var (a, _) = Pair();
        a.Recorder.Save(a.NewTransaction(1));
        a.Recorder.Save(a.NewTransaction(2));
        var status = a.Sync.SyncOnce();

        Assert.Equal(0, status.LastApplied.Applied + status.LastApplied.Ignored);
        Assert.Equal(new FileInfo(a.JournalPath).Length, a.Materializer.GetOffset(Path.GetFileName(a.JournalPath)));
        Assert.Equal(2, a.Reader.GetJournalInfos().Single().EventCount);
    }

    [Fact]
    public void Database_CanBeRebuiltFromJournalsAlone()
    {
        var (a, b) = Pair();
        var cat = new Category { Id = Guid.NewGuid(), Name = "Spożywcze", Color = "#00aa00" };
        a.Recorder.Save(cat);
        var ev = new SpecialEvent { Id = Guid.NewGuid(), Name = "Wakacje", DateFrom = new DateOnly(2026, 7, 1), DateTo = new DateOnly(2026, 7, 14), PlannedBudgetPln = 5000m };
        b.Recorder.Save(ev);
        var tx = a.NewTransaction(12.34m, "chleb", cat.Id) with
        {
            EventId = ev.Id, CurrencyCode = "EUR", ExchangeRateToPln = 4.2512m, AmountPln = Money.ToPln(12.34m, 4.2512m),
            Lines = [new TransactionLine { Id = Guid.NewGuid(), CategoryId = cat.Id, Amount = 12.34m, Description = "line" }],
        };
        a.Recorder.Save(tx);
        a.Recorder.Save(new Budget { Id = Guid.NewGuid(), CategoryId = cat.Id, MonthlyAmount = 800m, ValidFromMonth = new DateOnly(2026, 1, 1) });
        a.Recorder.Save(new RecurringTransaction { Id = Guid.NewGuid(), Amount = 50m, Note = "Netflix", Frequency = RecurrenceFrequency.Monthly, Day = 5, StartDate = new DateOnly(2026, 1, 5) });
        a.Recorder.Save(new CategoryRule { Id = Guid.NewGuid(), Pattern = "lewiatan", CategoryId = cat.Id, UseCount = 3 });
        var doomed = a.NewTransaction(99, "deleted");
        a.Recorder.Save(doomed);
        SyncBoth(a, b);
        a.Recorder.Delete(EntityTypes.Transaction, doomed.Id);
        SyncBoth(a, b);
        var expected = b.Snapshot();
        Assert.Equal(expected, a.Snapshot());

        // 1) Rebuild in place.
        b.Sync.Rebuild();
        Assert.Equal(expected, b.Snapshot());

        // 2) Brand new computer: same shared folder, empty local database.
        var fresh = _env.NewDevice("00000000-0000-0000-0000-00000000000c", _wall);
        fresh.Sync.SyncOnce();
        Assert.Equal(expected, fresh.Snapshot());

        var restored = fresh.Reader.GetTransaction(tx.Id)!;
        Assert.Equal("EUR", restored.CurrencyCode);
        Assert.Equal(4.2512m, restored.ExchangeRateToPln);
        Assert.Equal(52.46m, restored.AmountPln);
        Assert.Equal(12.34m, restored.Amount);
        Assert.Equal(new DateOnly(2026, 3, 15), restored.Date);
        Assert.Single(restored.Lines);
        Assert.True(fresh.Reader.IsTombstone(EntityTypes.Transaction, doomed.Id));
    }

    [Fact]
    public void ADevice_GetsItsOwnDataBackAfterLosingItsLocalDatabase()
    {
        var a = _env.NewDevice(TestEnv.DeviceA, _wall);
        a.Recorder.Save(a.NewTransaction(7, "keep"));
        var expected = a.Snapshot();
        a.Dispose();

        var again = _env.NewDevice(TestEnv.DeviceA, _wall, dbName: "local-a-new");
        again.Sync.SyncOnce();
        Assert.Equal(expected, again.Snapshot());

        // And the clock continues after everything it has already seen.
        var next = again.Recorder.Save(again.NewTransaction(8));
        Assert.True(next.Timestamp > new HlcTimestamp(_wall.Ms, 0));
    }

    [Fact]
    public void EventWithMismatchingPayloadId_OrUnknownType_IsCountedAsFailed_AndStateStaysUntouched()
    {
        var a = _env.NewDevice(TestEnv.DeviceA, _wall);
        var t = a.NewTransaction();
        var badId = new JournalEvent
        {
            EventId = Guid.NewGuid(), DeviceId = TestEnv.DeviceB, Timestamp = new HlcTimestamp(1, 0),
            EntityType = EntityTypes.Transaction, EntityId = Guid.NewGuid(), Operation = EventOperation.Upsert,
            Payload = JournalJson.ToPayload(t),
        };
        var unknown = badId with { EventId = Guid.NewGuid(), EntityType = "Spaceship", EntityId = t.Id };
        var r = a.Materializer.Apply([badId, unknown]);

        Assert.Equal(2, r.Failed);
        Assert.Empty(a.Reader.GetTransactions());
        Assert.False(a.Reader.IsTombstone(EntityTypes.Transaction, badId.EntityId));
    }

    [Fact]
    public async Task ConcurrentWritersAndSyncLoops_EndWithIdenticalData()
    {
        var (a, b) = Pair(sharedClock: false);
        a.Wall.Ms = 1_800_000_000_000; b.Wall.Ms = 1_800_000_000_000;
        const int perDevice = 150;

        Task Writer(Device d) => Task.Run(() =>
        {
            for (var i = 0; i < perDevice; i++)
            {
                d.Recorder.Save(d.NewTransaction(i + 1, $"{d.Identity.DeviceName}-{i}"));
                if (i % 10 == 0) d.Sync.SyncOnce();
            }
        });

        await Task.WhenAll(Writer(a), Writer(b));
        SyncBoth(a, b);
        b.Sync.SyncOnce();

        Assert.Equal(perDevice * 2, a.Reader.GetTransactions().Count);
        Assert.Equal(a.Snapshot(), b.Snapshot());
        Assert.Equal(SyncState.Synced, a.Sync.Status.State);
    }

    [Fact]
    public void Service_PicksUpOtherDevicesChanges_ByWatcherOrPolling()
    {
        var fast = new SyncOptions { PollInterval = TimeSpan.FromMilliseconds(300), Debounce = TimeSpan.FromMilliseconds(50) };
        var a = _env.NewDevice(TestEnv.DeviceA, _wall, fast);
        var b = _env.NewDevice(TestEnv.DeviceB, _wall, fast);
        a.Sync.Start();
        b.Sync.Start();

        var t = a.NewTransaction(42, "live");
        a.Recorder.Save(t);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (b.Reader.GetTransaction(t.Id) is null && DateTime.UtcNow < deadline) Thread.Sleep(50);

        Assert.NotNull(b.Reader.GetTransaction(t.Id));
        Assert.Equal(SyncState.Synced, b.Sync.Status.State);
        Assert.NotNull(b.Sync.Status.LastSyncUtc);
    }

    [Fact]
    public void StatusChanged_ReportsSyncingThenSynced()
    {
        var (a, b) = Pair();
        a.Recorder.Save(a.NewTransaction());
        var states = new List<SyncState>();
        b.Sync.StatusChanged += (_, s) => states.Add(s.State);
        b.Sync.SyncOnce();

        Assert.Equal([SyncState.Syncing, SyncState.Synced], states);
        Assert.Single(b.Sync.Status.Devices);
        Assert.Equal(TestEnv.DeviceA, b.Sync.Status.Devices[0].DeviceId);
        Assert.Equal(1, b.Sync.Status.Devices[0].EventCount);
    }

    // ----- OneDrive specifics -----

    [Fact]
    public void PartialLastLineFromOneDrive_IsLoadedOnTheNextSyncOnceComplete()
    {
        var (a, b) = Pair();
        a.Recorder.Save(a.NewTransaction(1, "complete"));

        var t2 = a.NewTransaction(2, "arrives in two chunks");
        var line = JournalJson.Serialize(new JournalEvent
        {
            EventId = Guid.NewGuid(), DeviceId = TestEnv.DeviceA, Timestamp = new HlcTimestamp(_wall.Ms + 5, 0),
            EntityType = EntityTypes.Transaction, EntityId = t2.Id, Operation = EventOperation.Upsert, Payload = JournalJson.ToPayload(t2),
        });
        var cut = line.Length * 2 / 3;
        File.AppendAllText(a.JournalPath, line[..cut]);

        var s1 = b.Sync.SyncOnce();
        Assert.Single(b.Reader.GetTransactions());
        Assert.Equal(SyncState.Synced, s1.State); // an unfinished line is not an error

        File.AppendAllText(a.JournalPath, line[cut..] + "\n");
        b.Sync.SyncOnce();
        Assert.Equal(2, b.Reader.GetTransactions().Count);
    }

    [Fact]
    public void CloudOnlyJournal_IsReportedWithAnIssue_AndReadAfterItIsDownloaded()
    {
        var cloudOnly = true;
        var opts = new SyncOptions { IsCloudOnly = f => cloudOnly && f.Name.Contains(TestEnv.DeviceA) };
        var a = _env.NewDevice(TestEnv.DeviceA, _wall);
        var b = _env.NewDevice(TestEnv.DeviceB, _wall, opts);
        a.Recorder.Save(a.NewTransaction());

        var s = b.Sync.SyncOnce();
        Assert.Equal(SyncState.Problem, s.State);
        var issue = Assert.Single(s.Issues);
        Assert.Equal(SyncIssueKind.CloudOnlyFile, issue.Kind);
        Assert.Empty(b.Reader.GetTransactions());

        cloudOnly = false;
        s = b.Sync.SyncOnce();
        Assert.Equal(SyncState.Synced, s.State);
        Assert.Single(b.Reader.GetTransactions());
    }

    [Fact]
    public void OneDriveConflictCopy_ProducesAWarning_NotSilentIgnoring()
    {
        var (a, b) = Pair();
        a.Recorder.Save(a.NewTransaction());
        File.Copy(a.JournalPath, Path.Combine(_env.Shared, $"journal_{TestEnv.DeviceA}-LAPTOP-XYZ.jsonl"));

        var s = b.Sync.SyncOnce();
        Assert.Equal(SyncState.Problem, s.State);
        var issue = Assert.Single(s.Issues);
        Assert.Equal(SyncIssueKind.ConflictCopy, issue.Kind);
        Assert.Contains("LAPTOP-XYZ", issue.FileName);
        Assert.Single(b.Reader.GetTransactions()); // the regular journal was still read
    }

    [Fact]
    public void NewerSchema_StopsWritesAndAsksForAnUpdate_WithoutTouchingData()
    {
        var (a, b) = Pair();
        a.Recorder.Save(a.NewTransaction(1, "old schema"));
        var future = JournalJson.Serialize(new JournalEvent
        {
            EventId = Guid.NewGuid(), DeviceId = TestEnv.DeviceA, Timestamp = new HlcTimestamp(_wall.Ms + 1, 0),
            EntityType = EntityTypes.Transaction, EntityId = Guid.NewGuid(), Operation = EventOperation.Upsert,
            Payload = System.Text.Json.Nodes.JsonNode.Parse("{\"somethingNew\":true}"), SchemaVersion = JournalEvent.CurrentSchemaVersion + 1,
        });
        File.AppendAllText(a.JournalPath, future + "\n");

        var s = b.Sync.SyncOnce();
        Assert.True(s.UpdateRequired);
        Assert.True(b.Guard.UpdateRequired);
        Assert.Single(b.Reader.GetTransactions());
        Assert.Throws<UpdateRequiredException>(() => b.Recorder.Save(b.NewTransaction()));
        Assert.Single(b.Reader.GetTransactions());
    }

    [Fact]
    public void MissingSharedFolder_IsReportedAsAProblem_AndRecoversWhenItReturns()
    {
        var gone = Path.Combine(_env.Root, "not-there");
        var a = _env.NewDevice(TestEnv.DeviceA, _wall);
        var svc = new SyncService(gone, a.Db, a.Materializer, a.Reader, a.Clock);

        var s = svc.SyncOnce();
        Assert.Equal(SyncState.Problem, s.State);
        Assert.Equal(SyncIssueKind.FolderUnavailable, Assert.Single(s.Issues).Kind);

        Directory.CreateDirectory(gone);
        Assert.Equal(SyncState.Synced, svc.SyncOnce().State);
    }

    [Fact]
    public void CorruptLineInAnotherDevicesJournal_IsReportedOnce_AndOtherEventsStillApply()
    {
        var (a, b) = Pair();
        a.Recorder.Save(a.NewTransaction(1));
        File.AppendAllText(a.JournalPath, "garbage that ends with a newline\n");
        a.Recorder.Save(a.NewTransaction(2));

        var s = b.Sync.SyncOnce();
        Assert.Equal(2, b.Reader.GetTransactions().Count);
        Assert.Equal(SyncIssueKind.CorruptLines, Assert.Single(s.Issues).Kind);
    }
}
