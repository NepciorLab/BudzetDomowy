using System.Net;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

/// <summary>NBP stand-in: publishes only on the given days.</summary>
public sealed class FakeRateSource : IExchangeRateSource
{
    private readonly Dictionary<DateOnly, decimal> _published;
    public int Calls { get; private set; }
    public bool Offline { get; set; }

    public FakeRateSource(params (string Date, decimal Rate)[] published) =>
        _published = published.ToDictionary(p => DateOnly.Parse(p.Date), p => p.Rate);

    public Task<IReadOnlyList<ExchangeRate>> GetRatesAsync(string currencyCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Calls++;
        if (Offline) throw new HttpRequestException("no network");
        IReadOnlyList<ExchangeRate> list = _published
            .Where(p => p.Key >= from && p.Key <= to)
            .Select(p => new ExchangeRate { CurrencyCode = currencyCode, Date = p.Key, Rate = p.Value, Source = ExchangeRateSource.Nbp, FetchedAt = DateTimeOffset.UtcNow })
            .OrderBy(r => r.Date).ToList();
        return Task.FromResult(list);
    }
}

public class ExchangeRateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-fx-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore Core(FakeRateSource source)
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        var c = BudgetCore.Create(Path.Combine(_root, "local" + _cores.Count), Path.Combine(_root, "shared"), "x", rateSource: source);
        _cores.Add(c);
        return c;
    }

    private static ExchangeRateService Service(BudgetCore c, FakeRateSource s, string today) =>
        new(c.Reader, s, () => DateOnly.Parse(today));

    // 2026-10-23 is a Friday
    private static readonly (string, decimal)[] Week =
    [
        ("2026-10-20", 4.20m), ("2026-10-21", 4.21m), ("2026-10-22", 4.22m), ("2026-10-23", 4.23m),
        ("2026-10-26", 4.26m), ("2026-10-27", 4.27m),
    ];

    [Theory]
    [InlineData("2026-10-26", "2026-10-23", 4.23)] // Monday -> Friday
    [InlineData("2026-10-25", "2026-10-23", 4.23)] // Sunday -> Friday
    [InlineData("2026-10-24", "2026-10-23", 4.23)] // Saturday -> Friday
    [InlineData("2026-10-27", "2026-10-26", 4.26)] // Tuesday -> Monday
    [InlineData("2026-10-23", "2026-10-22", 4.22)] // the rate of the same day is never used
    public async Task UsesTheLastPublishedDayBeforeTheTransactionDate(string date, string expectedDay, double expectedRate)
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        var r = await Service(core, src, "2026-10-30").GetRateAsync("EUR", DateOnly.Parse(date));

        Assert.True(r.HasRate);
        Assert.Equal(DateOnly.Parse(expectedDay), r.EffectiveDate);
        Assert.Equal((decimal)expectedRate, r.Rate);
        Assert.Equal(RateOrigin.Nbp, r.Origin);
    }

    [Fact]
    public async Task HolidayGap_FallsBackToTheLastDayBeforeTheHolidays()
    {
        // Christmas: nothing published on the 24th-26th (and the weekend)
        var src = new FakeRateSource(("2026-12-22", 4.30m), ("2026-12-23", 4.31m));
        var core = Core(src);
        var r = await Service(core, src, "2026-12-30").GetRateAsync("EUR", new DateOnly(2026, 12, 28));
        Assert.Equal(new DateOnly(2026, 12, 23), r.EffectiveDate);
        Assert.Equal(4.31m, r.Rate);
    }

    [Fact]
    public async Task LongGap_WidensTheSearchWindow()
    {
        var src = new FakeRateSource(("2026-09-20", 4.10m)); // nothing for five weeks
        var core = Core(src);
        var r = await Service(core, src, "2026-10-30").GetRateAsync("EUR", new DateOnly(2026, 10, 25));
        Assert.Equal(new DateOnly(2026, 9, 20), r.EffectiveDate);
        Assert.Equal(2, src.Calls);
    }

    [Fact]
    public async Task NoDataAtAll_GivesNone_WithoutThrowing()
    {
        var src = new FakeRateSource();
        var core = Core(src);
        var r = await Service(core, src, "2026-10-30").GetRateAsync("EUR", new DateOnly(2026, 10, 25));
        Assert.Equal(RateOrigin.None, r.Origin);
        Assert.False(r.HasRate);
        Assert.True(r.NeedsAttention);
    }

    [Fact]
    public async Task Pln_NeedsNoLookup()
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        var r = await Service(core, src, "2026-10-30").GetRateAsync("pln", new DateOnly(2026, 10, 25));
        Assert.Equal((RateOrigin.Pln, 1m), (r.Origin, r.Rate!.Value));
        Assert.Equal(0, src.Calls);
    }

    [Fact]
    public async Task SecondLookupForTheSameWeekend_ComesFromTheCache()
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        var svc = Service(core, src, "2026-10-30");

        await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 26)); // Monday: fetches
        Assert.Equal(1, src.Calls);

        var sunday = await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 25));
        var saturday = await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 24));
        var monday = await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 26));

        Assert.Equal(1, src.Calls);
        Assert.All(new[] { sunday, saturday, monday }, r => Assert.Equal(RateOrigin.Cache, r.Origin));
        Assert.Equal(4.23m, sunday.Rate);
    }

    [Fact]
    public async Task StaleCache_IsNotTrusted_WhenALaterDayMayHaveBeenPublished()
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        var svc = Service(core, src, "2026-10-30");

        // Cache only knows Friday (fetched when asking about the weekend) ...
        await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 25));
        // ... a transaction on Wednesday needs Tuesday's rate, which the cache is not known to cover.
        var r = await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 28));
        Assert.Equal(new DateOnly(2026, 10, 27), r.EffectiveDate);
        Assert.Equal(4.27m, r.Rate);
    }

    [Fact]
    public async Task Offline_UsesTheLastKnownRate_AndFlagsIt()
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        var svc = Service(core, src, "2026-10-30");
        await svc.GetRateAsync("EUR", new DateOnly(2026, 10, 26)); // fills the cache

        src.Offline = true;
        var r = await svc.GetRateAsync("EUR", new DateOnly(2026, 11, 20)); // far later: cache cannot be complete
        Assert.Equal(RateOrigin.LastKnown, r.Origin);
        Assert.True(r.NeedsAttention);
        Assert.Equal(4.23m, r.Rate); // newest cached rate (Friday 23.10: the Monday rate was never fetched)
        Assert.Equal(new DateOnly(2026, 10, 23), r.EffectiveDate);
        Assert.Contains("no network", r.Error);
    }

    [Fact]
    public async Task Offline_WithoutAnyCache_AsksForAManualRate()
    {
        var src = new FakeRateSource(Week) { Offline = true };
        var core = Core(src);
        var r = await Service(core, src, "2026-10-30").GetRateAsync("USD", new DateOnly(2026, 10, 26));
        Assert.Equal(RateOrigin.None, r.Origin);
        Assert.False(r.HasRate);
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowed()
    {
        var src = new FakeRateSource(Week);
        var core = Core(src);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var svc = new ExchangeRateService(core.Reader, new ThrowingSource(), () => new DateOnly(2026, 10, 30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetRateAsync("EUR", new DateOnly(2026, 10, 26), cts.Token));
    }

    private sealed class ThrowingSource : IExchangeRateSource
    {
        public Task<IReadOnlyList<ExchangeRate>> GetRatesAsync(string currencyCode, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException("x");
        }
    }

    [Fact]
    public void Rounding_ToPln_IsHalfAwayFromZero_AtTwoDecimals()
    {
        Assert.Equal(52.46m, Money.ToPln(12.34m, 4.2512m));   // 52.4598
        Assert.Equal(41.56m, Money.ToPln(10m, 4.15555m));      // 41.5555
        Assert.Equal(1.01m, Money.ToPln(1m, 1.005m));          // exactly .005 rounds up
        Assert.Equal(0.01m, Money.ToPln(0.01m, 0.5m));         // 0.005
        Assert.Equal(425.12m, Money.ToPln(100m, 4.2512m));
        Assert.Equal(0m, Money.ToPln(0m, 4.2512m));
    }

    [Fact]
    public void ForeignTransaction_StoresOriginalAmount_Rate_AndConvertedPln()
    {
        var src = new FakeRateSource();
        var core = Core(src);
        var t = core.Transactions.Add(new DateOnly(2026, 10, 26), TransactionType.Expense, 100m, null, "", null,
            place: "Hotel", currency: "eur", rateToPln: 4.2512m, rateDate: new DateOnly(2026, 10, 23));
        var stored = core.Reader.GetTransaction(t.Id)!;
        Assert.Equal("EUR", stored.CurrencyCode);
        Assert.Equal(100m, stored.Amount);
        Assert.Equal(4.2512m, stored.ExchangeRateToPln);
        Assert.Equal(new DateOnly(2026, 10, 23), stored.ExchangeRateDate);
        Assert.Equal(425.12m, stored.AmountPln);
        Assert.False(stored.ExchangeRateManual);

        // editing the amount keeps the rate and recomputes PLN
        core.Transactions.Update(stored with { Amount = 50m });
        Assert.Equal(212.56m, core.Reader.GetTransaction(t.Id)!.AmountPln);
    }

    [Fact]
    public void ManualRate_IsFlagged_AndPlnIgnoresRateInformation()
    {
        var core = Core(new FakeRateSource());
        var manual = core.Transactions.Add(new DateOnly(2026, 10, 26), TransactionType.Expense, 10m, null, "", null,
            currency: "USD", rateToPln: 3.9m, rateManual: true);
        Assert.True(core.Reader.GetTransaction(manual.Id)!.ExchangeRateManual);

        var pln = core.Transactions.Add(new DateOnly(2026, 10, 26), TransactionType.Expense, 10m, null, "", null,
            currency: "PLN", rateToPln: 9m, rateDate: new DateOnly(2026, 1, 1), rateManual: true);
        var stored = core.Reader.GetTransaction(pln.Id)!;
        Assert.Equal(1m, stored.ExchangeRateToPln);
        Assert.Null(stored.ExchangeRateDate);
        Assert.False(stored.ExchangeRateManual);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            core.Transactions.Add(new DateOnly(2026, 10, 26), TransactionType.Expense, 10m, null, "", null, currency: "USD", rateToPln: 0m));
    }

    [Fact]
    public void RecentCurrencies_AreOrderedByLastUse_AndCatalogPutsPlnFirst()
    {
        var core = Core(new FakeRateSource());
        var t0 = DateTimeOffset.UtcNow;
        void Add(string cur, int minutes)
        {
            var t = core.Transactions.Add(new DateOnly(2026, 10, 1), TransactionType.Expense, 1m, null, "", null, currency: cur, rateToPln: 4m);
            core.Transactions.Update(core.Reader.GetTransaction(t.Id)! with { CreatedAt = t0.AddMinutes(minutes) });
        }
        Add("EUR", 1); Add("USD", 2); Add("EUR", 3); Add("CZK", 4);

        var recent = core.Reader.GetRecentCurrencies();
        Assert.Equal(["CZK", "EUR", "USD"], recent);

        var ordered = CurrencyCatalog.Ordered(recent).Select(o => o.Code).ToList();
        Assert.Equal(["PLN", "CZK", "EUR", "USD"], ordered.Take(4));
        Assert.Equal(ordered.Count, ordered.Distinct().Count());
        Assert.Contains("GBP", ordered);
        Assert.True(string.CompareOrdinal(ordered[^2], ordered[^1]) < 0); // the rest is alphabetical
    }

    [Theory]
    [InlineData("12,50 EUR", 12.50, "EUR")]
    [InlineData("12.5eur", 12.5, "EUR")]
    [InlineData("9 €", 9, "EUR")]
    [InlineData("1 000,00 usd", 1000, "USD")]
    [InlineData("7,25", 7.25, null)]
    [InlineData("7,25 zł", 7.25, null)]
    [InlineData("7,25 PLN", 7.25, "PLN")]
    public void AmountWithCurrency_IsRecognised(string text, double amount, string? currency)
    {
        Assert.True(AmountParser.TryParseWithCurrency(text, CurrencyCatalog.Codes, out var a, out var c));
        Assert.Equal((decimal)amount, a);
        Assert.Equal(currency, c);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12 XYZ")]
    [InlineData("EUR")]
    public void AmountWithCurrency_RejectsGarbage(string text) =>
        Assert.False(AmountParser.TryParseWithCurrency(text, CurrencyCatalog.Codes, out _, out _));
}

public class NbpClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ParsesTableARates_AndBuildsTheDocumentedUrl()
    {
        const string json = """
            {"table":"A","currency":"euro","code":"EUR","rates":[
              {"no":"205/A/NBP/2026","effectiveDate":"2026-10-22","mid":4.2512},
              {"no":"206/A/NBP/2026","effectiveDate":"2026-10-23","mid":4.2488}]}
            """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        var client = new NbpClient(new HttpClient(handler));

        var rates = await client.GetRatesAsync("EUR", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));

        Assert.Equal(2, rates.Count);
        Assert.Equal(new DateOnly(2026, 10, 22), rates[0].Date);
        Assert.Equal(4.2512m, rates[0].Rate);
        Assert.Equal("EUR", rates[1].CurrencyCode);
        Assert.Equal("https://api.nbp.pl/api/exchangerates/rates/a/eur/2026-10-20/2026-10-24/?format=json", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task NotFound_MeansNoPublicationInTheRange()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var rates = await new NbpClient(new HttpClient(handler)).GetRatesAsync("EUR", new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 25));
        Assert.Empty(rates);
    }

    [Fact]
    public async Task ServerError_IsAnException_SoTheServiceCanFallBack()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new NbpClient(new HttpClient(handler)).GetRatesAsync("EUR", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));
    }
}

public class EventReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-ev-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        var c = BudgetCore.Create(Path.Combine(_root, "l" + _cores.Count), Path.Combine(_root, "shared"), "x", rateSource: new FakeRateSource());
        _cores.Add(c);
        return c;
    }

    private static readonly DateOnly D1 = new(2026, 7, 1);

    private (BudgetCore Core, SpecialEvent Event, Category Food, Category Fun) Scenario()
    {
        var core = NewCore();
        core.Categories.SeedIfEmpty();
        var cats = core.Reader.GetCategories();
        var food = cats.First(c => c.Name == "Spożywcze");
        var fun = cats.First(c => c.Name == "Rozrywka");
        var ev = core.Events.Add("Wakacje", D1, D1.AddDays(9), "#1E88E5", 1000m);
        return (core, ev, food, fun);
    }

    [Fact]
    public void Report_TotalsPerDayPlanAndCategorySlices()
    {
        var (core, ev, food, fun) = Scenario();
        var sub = core.Reader.GetCategories().First(c => c.ParentId == fun.Id); // a subcategory rolls up to its parent
        core.Transactions.Add(D1, TransactionType.Expense, 100m, food.Id, "", ev.Id);
        core.Transactions.Add(D1.AddDays(1), TransactionType.Expense, 50m, sub.Id, "", ev.Id);
        core.Transactions.Add(D1.AddDays(2), TransactionType.Expense, 50m, fun.Id, "", ev.Id);
        core.Transactions.Add(D1.AddDays(2), TransactionType.Expense, 40m, null, "", ev.Id);                // no category
        core.Transactions.Add(D1.AddDays(2), TransactionType.Income, 30m, null, "", ev.Id);                 // refund
        core.Transactions.Add(D1.AddDays(2), TransactionType.Expense, 999m, food.Id, "", null);              // not part of the event

        var today = D1.AddDays(3); // 4 days have started
        var r = Reports.ForEvent(ev, core.Reader.GetTransactions(), core.Reader.GetCategories(), today);

        Assert.Equal(5, r.TransactionCount);
        Assert.Equal(240m, r.ExpensePln);
        Assert.Equal(30m, r.IncomePln);
        Assert.Equal(4, r.Days);
        Assert.Equal(60m, r.ExpensePerDay);
        Assert.Equal(1000m, r.PlannedPln);
        Assert.Equal(0.24m, r.PlannedUsedFraction);
        Assert.Equal(760m, r.RemainingPln);

        Assert.Equal(["Spożywcze", "Rozrywka", ""], r.ByCategory.Select(s => s.Name));
        Assert.Equal([100m, 100m, 40m], r.ByCategory.Select(s => s.AmountPln).OrderByDescending(x => x));
        Assert.Equal(1m, Math.Round(r.ByCategory.Sum(s => s.Share), 6));
        Assert.Equal(100m, r.ByCategory.First(s => s.Name == "Rozrywka").AmountPln); // 50 + 50 rolled up
    }

    [Fact]
    public void Days_AreCountedOnlyUpToTodayAndNeverBeyondTheEnd()
    {
        var (core, ev, _, _) = Scenario();
        var all = core.Reader.GetTransactions();
        var cats = core.Reader.GetCategories();

        var before = Reports.ForEvent(ev, all, cats, D1.AddDays(-5));
        Assert.Equal(0, before.Days);
        Assert.Null(before.ExpensePerDay);

        Assert.Equal(1, Reports.ForEvent(ev, all, cats, D1).Days);
        Assert.Equal(10, Reports.ForEvent(ev, all, cats, D1.AddDays(9)).Days);
        Assert.Equal(10, Reports.ForEvent(ev, all, cats, D1.AddDays(100)).Days);
    }

    [Fact]
    public void WithoutAPlan_ThereIsNoComparison_AndOverspendingGoesNegative()
    {
        var (core, ev, food, _) = Scenario();
        var noPlan = core.Events.Save(ev with { PlannedBudgetPln = null });
        core.Transactions.Add(D1, TransactionType.Expense, 10m, food.Id, "", ev.Id);
        var r = Reports.ForEvent(noPlan, core.Reader.GetTransactions(), core.Reader.GetCategories(), D1);
        Assert.Null(r.PlannedUsedFraction);
        Assert.Null(r.RemainingPln);

        var small = core.Events.Save(ev with { PlannedBudgetPln = 8m });
        var over = Reports.ForEvent(small, core.Reader.GetTransactions(), core.Reader.GetCategories(), D1);
        Assert.Equal(1.25m, over.PlannedUsedFraction);
        Assert.Equal(-2m, over.RemainingPln);
    }

    [Fact]
    public void Report_UsesPlnAmounts_OfForeignCurrencyTransactions()
    {
        var (core, ev, food, _) = Scenario();
        core.Transactions.Add(D1, TransactionType.Expense, 10m, food.Id, "", ev.Id, currency: "EUR", rateToPln: 4.25m);
        var r = Reports.ForEvent(ev, core.Reader.GetTransactions(), core.Reader.GetCategories(), D1);
        Assert.Equal(42.50m, r.ExpensePln);
    }

    [Fact]
    public void EventTransactions_AreStillCountedInMonthlyCategoryTotals()
    {
        var (core, ev, food, _) = Scenario();
        core.Transactions.Add(D1, TransactionType.Expense, 30m, food.Id, "", ev.Id);   // during the event
        core.Transactions.Add(D1, TransactionType.Expense, 20m, food.Id, "", null);    // ordinary
        core.Transactions.Add(D1.AddMonths(1), TransactionType.Expense, 5m, food.Id, "", null); // other month

        var july = Reports.ByCategory(core.Reader.GetTransactions(), new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), TransactionType.Expense);
        Assert.Equal(50m, july.ByCategory[food.Id]);
        core.Transactions.Add(D1, TransactionType.Expense, 7m, null, "", null);
        var again = Reports.ByCategory(core.Reader.GetTransactions(), new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), TransactionType.Expense);
        Assert.Equal(7m, again.Uncategorized);
        Assert.Equal(57m, again.Total);
    }

    [Fact]
    public void DeletingAnEvent_DetachesItsTransactions_AndRestoreBringsEverythingBack()
    {
        var (core, ev, food, _) = Scenario();
        var t = core.Transactions.Add(D1, TransactionType.Expense, 30m, food.Id, "", ev.Id);

        var deletion = core.Events.Delete(ev.Id);
        Assert.Empty(core.Reader.GetSpecialEvents());
        Assert.Null(core.Reader.GetTransaction(t.Id)!.EventId);
        Assert.Single(core.Reader.GetTransactions()); // the transaction itself stays

        core.Events.Restore(deletion);
        Assert.Single(core.Reader.GetSpecialEvents());
        Assert.Equal(ev.Id, core.Reader.GetTransaction(t.Id)!.EventId);
    }

    [Fact]
    public void Events_SyncBetweenDevices_AndEndDateNeverPrecedesStart()
    {
        var a = NewCore();
        var b = NewCore();
        var ev = a.Events.Add("Weekend", new DateOnly(2026, 8, 8), new DateOnly(2026, 8, 1));
        Assert.Equal(new DateOnly(2026, 8, 8), ev.DateTo);

        a.Sync.SyncOnce(); b.Sync.SyncOnce();
        Assert.Equal("Weekend", Assert.Single(b.Reader.GetSpecialEvents()).Name);
    }
}
