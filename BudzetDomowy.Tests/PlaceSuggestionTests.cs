using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

public class PlaceSuggestionTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static Transaction At(string place, string date = "2026-10-01", TransactionType type = TransactionType.Expense) =>
        Make.Tx(date, type, 10m) with { Place = place };

    private static Transaction[] History() =>
    [
        At("Lewiatan", "2026-10-01"), At("lewiatan", "2026-10-05"), At("Lewiatan", "2026-10-09"),
        At("Lidl", "2026-10-20"), At("Lidl", "2026-10-21"),
        At("Leroy Merlin", "2026-10-25"),
        At("Pensja firmy", "2026-10-10", TransactionType.Income),
    ];

    [Fact]
    public void Build_RanksByNumberOfEntries_MergesSpellings_AndLeavesIncomeOut()
    {
        var list = PlaceSuggestionService.Build(History(), []);

        Assert.Equal(["Lewiatan", "Lidl", "Leroy Merlin"], list.Select(s => s.Name));
        Assert.Equal([3, 2, 1], list.Select(s => s.Weight));
        Assert.All(list, s => Assert.True(s.FromHistory));
    }

    [Fact]
    public void Build_SameWeight_TheMoreRecentlyUsedPlaceComesFirst()
    {
        var list = PlaceSuggestionService.Build([At("Stara", "2026-01-01"), At("Nowa", "2026-09-01")], []);
        Assert.Equal(["Nowa", "Stara"], list.Select(s => s.Name));
    }

    [Fact]
    public void Match_OffersFromTheFirstLetter_PrefixFirstThenWords_BestFirst_AndNotExactlyWhatIsTyped()
    {
        var all = PlaceSuggestionService.Build(History().Append(At("Biedronka Leclerc")), []);

        Assert.Equal(["Lewiatan", "Lidl", "Leroy Merlin"], PlaceSuggestionService.Match(all, "L").Select(s => s.Name).Take(3));
        Assert.Equal(["Lewiatan", "Leroy Merlin"], PlaceSuggestionService.Match(all, "le").Select(s => s.Name).Take(2));
        Assert.Equal("Biedronka Leclerc", PlaceSuggestionService.Match(all, "le").Last().Name);     // a word that starts with it comes after the places that start with it
        Assert.Empty(PlaceSuggestionService.Match(all, "Lidl"));                                    // nothing new to offer
        Assert.Empty(PlaceSuggestionService.Match(all, "  "));
        Assert.Empty(PlaceSuggestionService.Match(all, "xyz"));
        Assert.Equal(2, PlaceSuggestionService.Match(all, "L", max: 2).Count);
    }

    [Fact]
    public void Hints_AreTheDatabase_PlacesWithoutOneAreNotSuggested_AndWeightsCanBeChanged()
    {
        var hints = new[]
        {
            new PlaceHint { Id = PlaceSuggestionService.HintId("Leroy Merlin"), Name = "Leroy Merlin", Weight = 50 },
            new PlaceHint { Id = PlaceSuggestionService.HintId("lidl"), Name = "lidl", Hidden = true },
            new PlaceHint { Id = PlaceSuggestionService.HintId("Adaś"), Name = "Adaś", Weight = 4 },
            new PlaceHint { Id = PlaceSuggestionService.HintId("Lewiatan"), Name = "Lewiatan" },
        };
        var list = PlaceSuggestionService.Build(History(), hints);

        Assert.Equal(["Leroy Merlin", "Adaś", "Lewiatan"], list.Select(s => s.Name));
        Assert.True(list[0].WeightChanged);
        Assert.False(list[1].FromHistory);
        Assert.Equal(3, list[2].Weight);                       // the number of expenses, as nothing else was typed
    }

    [Fact]
    public void StartingWith_KeepsThePopularityOrder_IgnoresCase_AndSkipsTheExactPlace()
    {
        var all = PlaceSuggestionService.Build(History(), []);
        Assert.Equal(["Lewiatan", "Lidl", "Leroy Merlin"], PlaceSuggestionService.StartingWith(all, "L").Select(s => s.Name));
        Assert.Equal(["Lidl"], PlaceSuggestionService.StartingWith(all, "li").Select(s => s.Name));
        Assert.Empty(PlaceSuggestionService.StartingWith(all, "Lidl"));
        Assert.Empty(PlaceSuggestionService.StartingWith(all, " "));
        Assert.Single(PlaceSuggestionService.StartingWith(all, "L", max: 1));
    }

    [Fact]
    public void Import_AddsPlacesTypedEnoughTimes_AndNeverBringsBackARemovedOne()
    {
        var a = _env.NewDevice(TestEnv.DeviceA);
        foreach (var t in History()) a.Recorder.Save(t);
        var service = new PlaceSuggestionService(a.Recorder, a.Reader);

        Assert.False(service.IsInitialized);
        Assert.Equal(2, service.CountImportable(2));
        Assert.Equal(2, service.Import(2));                                   // Lewiatan (3) and Lidl (2), not Leroy Merlin (1)
        Assert.Equal(["Lewiatan", "Lidl"], service.All().Select(s => s.Name));
        Assert.Equal(0, service.Import(2));

        service.Remove("Lidl");
        Assert.Equal(1, service.Import(1));                                   // only Leroy Merlin: Lidl stays removed
        Assert.Equal(["Lewiatan", "Leroy Merlin"], service.All().Select(s => s.Name));
    }

    [Fact]
    public void Learn_AddsAPlaceTypedInANewExpense_OnlyOnceTheDatabaseExists_AndNotARemovedOne()
    {
        var a = _env.NewDevice(TestEnv.DeviceA);
        foreach (var t in History()) a.Recorder.Save(t);
        var service = new PlaceSuggestionService(a.Recorder, a.Reader);

        service.Learn("Nowy sklep");
        Assert.False(service.IsInitialized);                                  // nothing yet: every typed place is suggested anyway

        service.Import(2);
        service.Learn("  Nowy   sklep ");
        service.Learn("nowy sklep");
        Assert.Equal(1, service.All().Count(s => s.Name == "Nowy sklep"));

        service.Remove("Lidl");
        service.Learn("Lidl");
        Assert.DoesNotContain(service.All(), s => s.Name == "Lidl");
    }

    [Fact]
    public void EditingTheDatabaseTheFirstTime_KeepsTheOtherPlacesTypedSoFar()
    {
        var a = _env.NewDevice(TestEnv.DeviceA);
        foreach (var t in History()) a.Recorder.Save(t);
        var service = new PlaceSuggestionService(a.Recorder, a.Reader);

        service.Remove(["Lidl", "Leroy Merlin"]);
        Assert.Equal(["Lewiatan"], service.All().Select(s => s.Name));
    }

    [Fact]
    public void Service_SavesHints_ThatAnotherComputerSeesAfterSync()
    {
        var a = _env.NewDevice(TestEnv.DeviceA);
        var b = _env.NewDevice(TestEnv.DeviceB);
        foreach (var t in History()) a.Recorder.Save(t);
        var onA = new PlaceSuggestionService(a.Recorder, a.Reader);
        var onB = new PlaceSuggestionService(b.Recorder, b.Reader);

        onA.SetWeight("Lidl", 99);
        onA.Remove("Leroy Merlin");
        onA.Add("Adaś", 7);
        a.Sync.SyncOnce();
        b.Sync.SyncOnce();

        Assert.Equal(["Lidl", "Adaś", "Lewiatan"], onB.All().Select(s => s.Name));
        Assert.Equal(onA.All(), onB.All());

        onB.ResetWeight("Lidl");
        Assert.Equal(["Adaś", "Lewiatan", "Lidl"], onB.All().Select(s => s.Name));
        Assert.Equal(2, onB.All().First(s => s.Name == "Lidl").Weight);
    }

    [Fact]
    public void Remove_ThenAdd_BringsThePlaceBack()
    {
        var a = _env.NewDevice(TestEnv.DeviceA);
        foreach (var t in History()) a.Recorder.Save(t);
        var service = new PlaceSuggestionService(a.Recorder, a.Reader);

        service.Remove("Lidl");
        Assert.DoesNotContain(service.All(), s => s.Name == "Lidl");
        service.Add("Lidl", 5);
        Assert.Equal(5, service.All().First(s => s.Name == "Lidl").Weight);
    }

    [Fact]
    public void PlaceHintEvents_AreWrittenWithTheNewSchema_SoOlderProgramsOfferTheUpdate()
    {
        Assert.Equal(JournalEvent.PlaceHintSchemaVersion, EntityTypes.SchemaOf(EntityTypes.PlaceHint));
        Assert.Equal(2, EntityTypes.SchemaOf(EntityTypes.Payee));
        Assert.Equal(JournalEvent.BaseSchemaVersion, EntityTypes.SchemaOf(EntityTypes.Transaction));
        Assert.Equal(JournalEvent.BaseSchemaVersion, EntityTypes.SchemaOf(EntityTypes.RecurringTransaction, new RecurringTransaction { Frequency = RecurrenceFrequency.Weekly }));
        Assert.Equal(JournalEvent.PlaceHintSchemaVersion, EntityTypes.SchemaOf(EntityTypes.RecurringTransaction, new RecurringTransaction { Frequency = RecurrenceFrequency.EveryDays }));
        Assert.True(JournalEvent.CurrentSchemaVersion >= JournalEvent.PlaceHintSchemaVersion);
    }
}

public class DayPeriodTests
{
    [Fact]
    public void Day_IsOneDay_AndStepsByDays()
    {
        var d = new DateOnly(2026, 10, 26);
        Assert.Equal((d, d), PeriodRanges.Range(PeriodKind.Day, d));
        Assert.Equal(new DateOnly(2026, 10, 25), PeriodRanges.Shift(PeriodKind.Day, d, -1));
        Assert.Equal(new DateOnly(2026, 11, 1), PeriodRanges.Shift(PeriodKind.Day, d, 6));
    }
}

public class EveryDaysRecurrenceTests
{
    private static RecurringTransaction Every(int days, string start, string? end = null) => new()
    {
        Frequency = RecurrenceFrequency.EveryDays, Day = days, StartDate = DateOnly.Parse(start), EndDate = end is null ? null : DateOnly.Parse(end),
    };

    private static List<string> Dates(RecurringTransaction t, string from, string to) =>
        RecurrenceEngine.Occurrences(t, DateOnly.Parse(from), DateOnly.Parse(to)).Select(d => d.ToString("yyyy-MM-dd")).ToList();

    [Fact]
    public void EveryThreeDays_StartsOnTheStartDate_AndKeepsTheRhythm()
    {
        var t = Every(3, "2026-10-25");
        Assert.Equal(["2026-10-25", "2026-10-28", "2026-10-31", "2026-11-03"], Dates(t, "2026-10-01", "2026-11-05"));
        Assert.Equal(["2026-10-31", "2026-11-03"], Dates(t, "2026-10-29", "2026-11-05"));      // a later "from" does not shift the rhythm
        Assert.Equal(["2026-10-28"], Dates(t, "2026-10-28", "2026-10-30"));
        Assert.Empty(Dates(t, "2026-10-29", "2026-10-30"));
    }

    [Fact]
    public void EveryDays_StopsAtTheEndDate_AndOneDayMeansDaily()
    {
        Assert.Equal(["2026-10-25", "2026-10-26", "2026-10-27"], Dates(Every(1, "2026-10-25", "2026-10-27"), "2026-10-01", "2026-12-31"));
        Assert.Equal(["2026-10-25", "2026-11-01"], Dates(Every(7, "2026-10-25", "2026-11-05"), "2026-10-01", "2026-12-31"));
        Assert.Equal(["2026-10-25"], Dates(Every(0, "2026-10-25", "2026-10-25"), "2026-10-01", "2026-12-31"));   // a broken 0 is treated as 1
    }
}
