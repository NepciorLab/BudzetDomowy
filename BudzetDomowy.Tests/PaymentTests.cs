using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

public sealed class PaymentTests : IDisposable
{
    private const string ValidAccount = "61109010140000071219812874";           // the standard example of a Polish IBAN

    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-pay-" + Guid.NewGuid().ToString("N"));
    private readonly List<BudgetCore> _cores = [];

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BudgetCore NewCore(string name = "a")
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var core = BudgetCore.Create(Path.Combine(_root, "local-" + name), data, name);
        core.Categories.SeedIfEmpty();
        _cores.Add(core);
        return core;
    }

    private static DateOnly D(string s) => DateOnly.Parse(s);

    // ----- the account number -----

    [Theory]
    [InlineData("61109010140000071219812874")]
    [InlineData("61 1090 1014 0000 0712 1981 2874")]
    [InlineData("PL61 1090 1014 0000 0712 1981 2874")]
    [InlineData("pl61-1090-1014-0000-0712-1981-2874")]
    [InlineData("  61109010140000071219812874  ")]
    public void AccountNumber_AcceptsTheUsualWaysOfWritingIt(string typed)
    {
        Assert.True(BankAccount.TryParseValid(typed, out var digits));
        Assert.Equal(ValidAccount, digits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("61109010140000071219812875")]            // one digit different: the check number fails
    [InlineData("6110901014000007121981287")]             // too short
    [InlineData("611090101400000712198128741")]           // too long
    [InlineData("61109010140000071219812874x")]           // a letter
    [InlineData("DE89370400440532013000")]                // not a Polish number
    [InlineData("00000000000000000000000000")]
    public void AccountNumber_RejectsWrongOnes(string typed) => Assert.False(BankAccount.TryParseValid(typed, out _));

    [Fact]
    public void AccountNumber_IsShownInTheStandardGroups()
    {
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", BankAccount.Format(ValidAccount));
        Assert.Equal("61 1090 1014", BankAccount.Format("6110901014"));                 // also while it is being typed
        Assert.Equal("6", BankAccount.Format("6"));
        Assert.Equal("61", BankAccount.Format("61"));
        Assert.Equal("61 1", BankAccount.Format("611"));
        Assert.Equal("", BankAccount.Format(null));
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", BankAccount.Format("PL61-1090 1014 00000712198128 74"));   // other characters are ignored
    }

    [Fact]
    public void AccountNumber_NormalizeReturnsWhatItUnderstoodEvenWhenWrong()
    {
        Assert.False(BankAccount.TryNormalize("61 1090 1014", out var digits));
        Assert.Equal("611090101 4".Replace(" ", ""), digits);
        Assert.False(BankAccount.IsValid(null));
        Assert.False(BankAccount.IsValid("abc"));
    }

    // ----- the QR code -----

    [Fact]
    public void Qr_HasTheFieldsOfTheRecommendationInOrder()
    {
        var qr = PaymentQr.Build(ValidAccount, "Elektrownia SA", "Faktura FV/2026/10", 123.45m, nip: "5214349636");
        Assert.Equal($"5214349636|PL|{ValidAccount}|012345|Elektrownia SA|Faktura FV/2026/10|||", qr.Text);
        Assert.False(qr.Truncated);
        Assert.False(qr.AmountOmitted);
        Assert.Equal(8, qr.Text.Count(c => c == '|'));                                  // nine fields

        var noNip = PaymentQr.Build(ValidAccount, "Tauron", "Prąd 10/2026", 20.30m);
        Assert.Equal($"|PL|{ValidAccount}|002030|Tauron|Prąd 10/2026|||", noNip.Text);   // an empty first field, letters with ogonek kept
    }

    [Fact]
    public void Qr_ShortensLongTextsAndDropsWhatTheStandardDoesNotAllow()
    {
        var qr = PaymentQr.Build(ValidAccount, "Spółdzielnia Mieszkaniowa Osiedle Zielone", "Czynsz za październik 2026 – mieszkanie nr 12", 1m);
        var fields = qr.Text.Split('|');
        Assert.True(qr.Truncated);
        Assert.Equal(20, fields[4].Length);
        Assert.True(fields[5].Length <= 32);
        Assert.Equal("Spółdzielnia Mieszka", fields[4]);

        var cleaned = PaymentQr.Build(ValidAccount, "A|B", "Faktura (12:2026) <test>", 5m);
        Assert.Equal("AB", cleaned.Text.Split('|')[4]);                                 // the delimiter can never get into a field
        Assert.Equal("Faktura 122026 test", cleaned.Text.Split('|')[5]);
        Assert.True(cleaned.Truncated);
        Assert.Equal(8, cleaned.Text.Count(c => c == '|'));
    }

    [Fact]
    public void Qr_AnAmountThatDoesNotFitSixDigitsIsLeftOut()
    {
        Assert.Equal("999999", PaymentQr.Build(ValidAccount, "X", "Y", 9999.99m).Text.Split('|')[3]);
        var big = PaymentQr.Build(ValidAccount, "X", "Y", 10000m);
        Assert.Equal("000000", big.Text.Split('|')[3]);
        Assert.True(big.AmountOmitted);
        Assert.Equal("000001", PaymentQr.Build(ValidAccount, "X", "Y", 0.01m).Text.Split('|')[3]);
        Assert.Equal("012346", PaymentQr.Build(ValidAccount, "X", "Y", 123.455m).Text.Split('|')[3]);   // rounded to the grosz
    }

    // ----- payees and payments -----

    [Fact]
    public void Payees_NeedANameAndACorrectAccount_AndAreStoredWithoutSpaces()
    {
        var core = NewCore();
        Assert.Throws<ArgumentException>(() => core.Payments.AddPayee("  ", ValidAccount));
        Assert.Throws<ArgumentException>(() => core.Payments.AddPayee("Tauron", "61109010140000071219812875"));

        var payee = core.Payments.AddPayee("  Tauron ", "61 1090 1014 0000 0712 1981 2874");
        Assert.Equal(("Tauron", ValidAccount), (payee.Name, payee.AccountNumber));
        Assert.Equal(payee, core.Reader.GetPayees().Single());
    }

    [Fact]
    public void Payments_AreAddedPendingInOrderOfTheirDates()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Elektrownia", ValidAccount);
        core.Payments.AddPayments(payee.Id,
        [
            new NewPayment(D("2026-11-20"), "Rata 2", 150m),
            new NewPayment(D("2026-10-30"), "Rata 1", 149.99m),
        ]);

        var list = core.Reader.GetPlannedPayments();
        Assert.Equal(["Rata 1", "Rata 2"], list.Select(p => p.Title));
        Assert.All(list, p => Assert.Equal(PaymentStatus.Pending, p.Status));
        Assert.Equal(149.99m, list[0].Amount);
        Assert.Throws<ArgumentException>(() => core.Payments.AddPayments(Guid.NewGuid(), [new NewPayment(D("2026-11-01"), "x", 1m)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-01"), "x", 0m)]));
    }

    [Fact]
    public void Reminders_StartThreeDaysBeforeTheDateAndContinueWhenLate_UntilThePaymentIsDealtWith()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Elektrownia", ValidAccount);
        var added = core.Payments.AddPayments(payee.Id,
        [
            new NewPayment(D("2026-10-30"), "Dziś za 4 dni", 10m),
            new NewPayment(D("2026-10-29"), "Za 3 dni", 20m),
            new NewPayment(D("2026-10-26"), "Jutro", 30m),
            new NewPayment(D("2026-10-20"), "Spóźniona", 40m),
        ]);
        var today = D("2026-10-26");

        Assert.Equal(["Spóźniona", "Jutro", "Za 3 dni"], core.Payments.DueForReminder(today).Select(p => p.Title));    // "in 4 days" is not yet due

        // scheduled: no more reminders, but it stays on the list
        core.Payments.MarkScheduled(added.Single(p => p.Title == "Jutro").Id);
        Assert.Equal(["Spóźniona", "Za 3 dni"], core.Payments.DueForReminder(today).Select(p => p.Title));
        Assert.Equal(4, core.Reader.GetPlannedPayments().Count);
        Assert.Equal(PaymentStatus.Scheduled, core.Reader.GetPlannedPayments().Single(p => p.Title == "Jutro").Status);

        // back to "to pay": the reminders return
        core.Payments.MarkPending(added.Single(p => p.Title == "Jutro").Id);
        Assert.Equal(3, core.Payments.DueForReminder(today).Count);
    }

    [Fact]
    public void Paying_BooksAnExpenseWithTheCurrentDate_AndTakesTheBillOffTheList()
    {
        var core = NewCore();
        var food = core.Reader.GetCategories().First(c => c.Name == "Rachunki").Id;
        var payee = core.Payments.AddPayee("Elektrownia", ValidAccount, food);
        var payment = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-10-15"), "Faktura 10/2026", 312.40m)]).Single();

        var transaction = core.Payments.MarkPaid(payment.Id, D("2026-10-26"));

        Assert.NotNull(transaction);
        Assert.Equal((D("2026-10-26"), TransactionType.Expense, 312.40m, food, "Elektrownia", "Faktura 10/2026"),
            (transaction!.Date, transaction.Type, transaction.Amount, transaction.CategoryId, transaction.Place, transaction.Note));
        Assert.Empty(core.Reader.GetPlannedPayments());
        Assert.Single(core.Reader.GetTransactions(), t => t.Id == transaction.Id);
        Assert.Null(core.Payments.MarkPaid(payment.Id));                                  // already gone
    }

    [Fact]
    public void Paying_WithoutACategoryOfTheBill_UsesTheOneThePlaceWasBookedUnderBefore()
    {
        var core = NewCore();
        var cat = core.Reader.GetCategories().First(c => c.Name == "Rachunki").Id;
        core.Transactions.Add(D("2026-09-20"), TransactionType.Expense, 100m, cat, "", null, place: "Wodociągi");
        var payee = core.Payments.AddPayee("wodociągi", ValidAccount);
        var payment = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-10-28"), "Woda", 55m)]).Single();

        Assert.Equal(cat, core.Payments.MarkPaid(payment.Id, D("2026-10-26"))!.CategoryId);

        var stranger = core.Payments.AddPayee("Nowy odbiorca", ValidAccount);
        var other = core.Payments.AddPayments(stranger.Id, [new NewPayment(D("2026-10-28"), "?", 5m)]).Single();
        Assert.Null(core.Payments.MarkPaid(other.Id, D("2026-10-26"))!.CategoryId);       // nothing known: uncategorised
    }

    [Fact]
    public void ScheduledPayments_CountAsPaidOnTheirDateOnceItHasPassed()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Czynsz", ValidAccount);
        var added = core.Payments.AddPayments(payee.Id,
        [
            new NewPayment(D("2026-10-24"), "Przeszła", 800m),
            new NewPayment(D("2026-10-26"), "Dziś", 810m),
            new NewPayment(D("2026-10-28"), "Przyszła", 820m),
            new NewPayment(D("2026-10-10"), "Niezaplanowana, spóźniona", 830m),
        ]);
        foreach (var p in added.Where(p => p.Title != "Niezaplanowana, spóźniona")) core.Payments.MarkScheduled(p.Id);

        Assert.Equal(1, core.Payments.SettleScheduled(D("2026-10-26")));                   // only the one that is strictly in the past

        var booked = Assert.Single(core.Reader.GetTransactions());
        Assert.Equal((D("2026-10-24"), 800m, "Czynsz"), (booked.Date, booked.Amount, booked.Place));   // on the date it was set for
        Assert.Equal(["Niezaplanowana, spóźniona", "Dziś", "Przyszła"], core.Reader.GetPlannedPayments().Select(p => p.Title).ToList().OrderBy(t => t == "Dziś" ? 1 : t == "Przyszła" ? 2 : 0));

        Assert.Equal(0, core.Payments.SettleScheduled(D("2026-10-26")));                   // nothing twice
        Assert.Equal(2, core.Payments.SettleScheduled(D("2026-10-30")));
        Assert.Equal(3, core.Reader.GetTransactions().Count);
        Assert.Equal("Niezaplanowana, spóźniona", Assert.Single(core.Reader.GetPlannedPayments()).Title);   // a payment never marked stays and keeps reminding
    }

    [Fact]
    public void TwoComputersSettlingTheSameBill_MakeOneTransaction()
    {
        var core = NewCore("a");
        var payee = core.Payments.AddPayee("Gaz", ValidAccount);
        var payment = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-10-24"), "Gaz", 90m)]).Single();
        core.Payments.MarkScheduled(payment.Id);

        var other = NewCore("b");
        other.Sync.SyncOnce();                                                             // the second computer learns about the bill
        core.Sync.SyncOnce();
        Assert.Single(other.Reader.GetPlannedPayments());

        Assert.Equal(1, core.Payments.SettleScheduled(D("2026-10-26")));
        Assert.Equal(1, other.Payments.SettleScheduled(D("2026-10-26")));                  // both did it before seeing the other's work
        core.Sync.SyncOnce();
        other.Sync.SyncOnce();

        Assert.Single(core.Reader.GetTransactions());
        Assert.Single(other.Reader.GetTransactions());
        Assert.Empty(core.Reader.GetPlannedPayments());
        Assert.Empty(other.Reader.GetPlannedPayments());
    }

    [Fact]
    public void Payees_InUseCannotBeDeleted()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Gaz", ValidAccount);
        var payment = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-24"), "Gaz", 90m)]).Single();

        Assert.False(core.Payments.DeletePayee(payee.Id));
        Assert.Single(core.Reader.GetPayees());

        core.Payments.DeletePayment(payment.Id);
        Assert.True(core.Payments.DeletePayee(payee.Id));
        Assert.Empty(core.Reader.GetPayees());
    }

    // ----- storage and synchronisation -----

    [Fact]
    public void PaymentsSurviveARebuildFromTheJournals()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Elektrownia", ValidAccount);
        core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-05"), "Rata", 42.42m)]);

        core.Sync.Rebuild();

        Assert.Equal(("Elektrownia", ValidAccount), (core.Reader.GetPayees().Single().Name, core.Reader.GetPayees().Single().AccountNumber));
        var payment = core.Reader.GetPlannedPayments().Single();
        Assert.Equal((D("2026-11-05"), "Rata", 42.42m, PaymentStatus.Pending), (payment.DueDate, payment.Title, payment.Amount, payment.Status));
    }

    [Fact]
    public void OnlyTheNewKindsNeedANewerProgram_OlderOnesKeepReadingTheOtherEvents()
    {
        var core = NewCore();
        var cat = core.Reader.GetCategories().First().Id;
        core.Transactions.Add(D("2026-10-01"), TransactionType.Expense, 5m, cat, "", null);
        var payee = core.Payments.AddPayee("Elektrownia", ValidAccount);
        core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-05"), "Rata", 1m)]);

        var path = JournalFiles.PathFor(core.DataFolder, core.Device.DeviceId);
        var all = JournalReader.ReadFrom(path, 0);
        Assert.Equal(0, all.CorruptLines);
        Assert.Null(all.UnsupportedSchemaVersion);
        Assert.Equal([1, 2, 2], all.Events.Where(e => e.EntityType is "Transaction" or "Payee" or "PlannedPayment").Select(e => e.SchemaVersion));

        // an older program (it supports schema 1): it reads the transaction, then stops and asks for an update at the first payment event
        var old = JournalReader.ReadFrom(path, 0, maxSupportedSchema: 1);
        Assert.Equal(2, old.UnsupportedSchemaVersion);
        Assert.Contains(old.Events, e => e.EntityType == "Transaction");
        Assert.DoesNotContain(old.Events, e => e.EntityType is "Payee" or "PlannedPayment");
    }

    // ----- series: payments typed together are edited together -----

    [Fact]
    public void PaymentsTypedTogether_FormASeries()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Tauron", ValidAccount);
        var first = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-10"), "Rata 1", 100m), new NewPayment(D("2026-12-10"), "Rata 2", 100m)]);
        var other = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-20"), "Gaz", 50m)]);

        Assert.NotNull(first[0].SeriesId);
        Assert.Equal(first[0].SeriesId, first[1].SeriesId);
        Assert.NotEqual(first[0].SeriesId, other[0].SeriesId);
        Assert.Equal(["Rata 1", "Rata 2"], core.Payments.SeriesOf(first[1].Id).Select(p => p.Title));
        Assert.Equal(["Gaz"], core.Payments.SeriesOf(other[0].Id).Select(p => p.Title));
        Assert.Empty(core.Payments.SeriesOf(Guid.NewGuid()));
    }

    [Fact]
    public void SaveSeries_ChangesAddsAndRemoves_AndAChangedPaymentGoesBackToPending()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Tauron", ValidAccount);
        var added = core.Payments.AddPayments(payee.Id, [
            new NewPayment(D("2026-11-10"), "Rata 1", 100m), new NewPayment(D("2026-12-10"), "Rata 2", 100m), new NewPayment(D("2027-01-10"), "Rata 3", 100m)]);
        core.Payments.MarkScheduled(added[0].Id);
        core.Payments.MarkScheduled(added[1].Id);

        core.Payments.SaveSeries(added[0].Id, payee.Id, [
            new SeriesRow(added[0].Id, D("2026-11-10"), "Rata 1", 100m),             // unchanged: stays scheduled
            new SeriesRow(added[1].Id, D("2026-12-12"), "Rata 2", 120m),             // changed: back to "to pay"
            new SeriesRow(null, D("2027-02-10"), " Rata 4 ", 90m),                   // new
        ]);                                                                          // Rata 3 is not there: removed

        var list = core.Reader.GetPlannedPayments();
        Assert.Equal(["Rata 1", "Rata 2", "Rata 4"], list.Select(p => p.Title));
        Assert.Equal(PaymentStatus.Scheduled, list[0].Status);
        Assert.Equal((PaymentStatus.Pending, D("2026-12-12"), 120m), (list[1].Status, list[1].DueDate, list[1].Amount));
        Assert.Single(list.Select(p => p.SeriesId).Distinct());
        Assert.Equal(added[0].SeriesId, list[2].SeriesId);
        Assert.Equal(list[1].Id, added[1].Id);                                       // the same payments, edited in place
    }

    [Fact]
    public void SaveSeries_CanChangeThePayee_AndSeriesWithoutAnIdGetsOne()
    {
        var core = NewCore();
        var one = core.Payments.AddPayee("Jeden", ValidAccount);
        var two = core.Payments.AddPayee("Dwa", ValidAccount);
        var added = core.Payments.AddPayments(one.Id, [new NewPayment(D("2026-11-10"), "Rachunek", 10m)]);
        core.Payments.MarkScheduled(added[0].Id);

        core.Payments.SaveSeries(added[0].Id, two.Id, [new SeriesRow(added[0].Id, D("2026-11-10"), "Rachunek", 10m)]);
        var now = Assert.Single(core.Reader.GetPlannedPayments());
        Assert.Equal((two.Id, PaymentStatus.Pending), (now.PayeeId, now.Status));

        // a payment typed alone by an older version has no series id: saving it assigns one
        var legacy = new PlannedPayment { Id = Guid.NewGuid(), PayeeId = one.Id, DueDate = D("2026-11-30"), Title = "Stary", Amount = 5m };
        core.Recorder.Save(legacy);
        Assert.Equal(["Stary"], core.Payments.SeriesOf(legacy.Id).Select(p => p.Title));
        core.Payments.SaveSeries(legacy.Id, one.Id, [new SeriesRow(legacy.Id, D("2026-11-30"), "Stary", 5m), new SeriesRow(null, D("2026-12-30"), "Stary", 5m)]);
        var series = core.Payments.SeriesOf(legacy.Id);
        Assert.Equal(2, series.Count);
        Assert.NotNull(series[0].SeriesId);
        Assert.Equal(series[0].SeriesId, series[1].SeriesId);
        Assert.Equal(3, core.Reader.GetPlannedPayments().Count);                        // plus the earlier payment, untouched
    }

    [Fact]
    public void SaveSeries_DoesNotBringBackAPaymentPaidInTheMeantime()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Tauron", ValidAccount);
        var added = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-10"), "Rata 1", 100m), new NewPayment(D("2026-12-10"), "Rata 2", 100m)]);
        core.Payments.MarkPaid(added[0].Id, D("2026-11-09"));

        core.Payments.SaveSeries(added[1].Id, payee.Id, [new SeriesRow(added[0].Id, D("2026-11-10"), "Rata 1", 100m), new SeriesRow(added[1].Id, D("2026-12-10"), "Rata 2", 100m)]);

        Assert.Equal(["Rata 2"], core.Reader.GetPlannedPayments().Select(p => p.Title));
        Assert.Single(core.Reader.GetTransactions());
    }

    [Fact]
    public void TheSeriesSurvivesARebuildFromTheJournals()
    {
        var core = NewCore();
        var payee = core.Payments.AddPayee("Tauron", ValidAccount);
        var added = core.Payments.AddPayments(payee.Id, [new NewPayment(D("2026-11-10"), "Rata 1", 100m), new NewPayment(D("2026-12-10"), "Rata 2", 100m)]);

        core.Sync.Rebuild();

        var list = core.Reader.GetPlannedPayments();
        Assert.Equal(added[0].SeriesId, list[0].SeriesId);
        Assert.Equal(list[0].SeriesId, list[1].SeriesId);
    }
}
