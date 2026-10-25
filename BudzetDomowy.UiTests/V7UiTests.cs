using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.UiTests;

/// <summary>Planning payments: payees, bills, the QR code, reminders, scheduled payments.</summary>
public class V7UiTests
{
    private const string Account = "61109010140000071219812874";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static Payee AddPayee(TestApp app, string name = "Elektrownia SA", Guid? category = null) => app.Core.Payments.AddPayee(name, Account, category);

    private static PlannedPayment AddBill(TestApp app, Payee payee, int daysFromToday, string title = "Faktura", decimal amount = 100m) =>
        app.Core.Payments.AddPayments(payee.Id, [new NewPayment(Today.AddDays(daysFromToday), title, amount)]).Single();

    // ----- the payee -----

    [Fact]
    public void Payee_TheAccountIsCheckedAndShownInGroups() => WithApp(app =>
    {
        var vm = new PayeeEditViewModel(app.Core, null);
        Assert.Equal("", vm.AccountHint);
        Assert.Equal("None", vm.AccountState);

        vm.AccountText = "6110901014";
        Assert.Contains("10 z 26", vm.AccountHint);
        Assert.Equal("Bad", vm.AccountState);

        vm.AccountText = "61109010140000071219812875";
        Assert.Contains("Suma kontrolna", vm.AccountHint);
        Assert.Equal("Bad", vm.AccountState);

        vm.AccountText = "61 1090 abc";
        Assert.Contains("tylko cyfry", vm.AccountHint);

        vm.AccountText = "PL61109010140000071219812874";
        Assert.Contains("poprawny", vm.AccountHint);
        Assert.Equal("Ok", vm.AccountState);

        vm.FormatAccount();                                                              // the box is left: the standard groups
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", vm.AccountText);

        vm.AccountText = "6110 9010x";
        vm.FormatAccount();
        Assert.Equal("6110 9010x", vm.AccountText);                                      // typing with a wrong sign is not rewritten under the user's hands
    });

    [Fact]
    public void Payee_IsSavedOnlyWithANameAndACorrectNumber() => WithApp(app =>
    {
        var vm = new PayeeEditViewModel(app.Core, null);
        var saved = 0;
        vm.Saved += () => saved++;

        vm.SaveCommand.Execute(null);
        Assert.Contains("nazwę", vm.ErrorMessage);
        vm.Name = "Tauron";
        vm.SaveCommand.Execute(null);
        Assert.Contains("numer rachunku", vm.ErrorMessage);
        vm.AccountText = "61109010140000071219812875";
        vm.SaveCommand.Execute(null);
        Assert.Contains("niepoprawny", vm.ErrorMessage);
        Assert.Equal(0, saved);
        Assert.Empty(app.Core.Reader.GetPayees());

        vm.AccountText = "61 1090 1014 0000 0712 1981 2874";
        vm.CategoryId = vm.Categories.First(c => c.Category.Name == "Rachunki").Id;
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, saved);
        var payee = Assert.Single(app.Core.Reader.GetPayees());
        Assert.Equal(("Tauron", Account, vm.CategoryId), (payee.Name, payee.AccountNumber, payee.CategoryId));
        Assert.Equal(payee, vm.Result);
    });

    [Fact]
    public void Payee_CanBeEdited_AndTheEditorShowsTheStoredNumberInGroups() => WithApp(app =>
    {
        var payee = AddPayee(app);
        var vm = new PayeeEditViewModel(app.Core, payee);
        Assert.Equal("Odbiorca płatności", vm.Title);
        Assert.Equal("Elektrownia SA", vm.Name);
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", vm.AccountText);

        vm.Name = "Elektrownia Zielona";
        vm.SaveCommand.Execute(null);
        Assert.Equal("Elektrownia Zielona", Assert.Single(app.Core.Reader.GetPayees()).Name);
        Assert.Equal(payee.Id, vm.Result!.Id);                                            // the same payee, not a new one
    });

    [Fact]
    public void Payees_TheListAddsEditsAndDeletes_ButNotAPayeeWithBills() => WithApp(app =>
    {
        var dialogs = app.Get<FakePaymentDialogs>();
        var messages = app.Get<FakeMessageDialogs>();
        var gas = AddPayee(app, "Gaz");
        var water = AddPayee(app, "Woda");
        AddBill(app, gas, 10);

        var vm = new PayeesViewModel(app.Core, dialogs, messages);
        Assert.Equal(["Gaz", "Woda"], vm.Items.Select(i => i.Payee.Name));
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", vm.Items[0].AccountText);

        vm.Selected = vm.Items[0];
        vm.DeleteCommand.Execute(null);
        Assert.Contains("ma jeszcze płatności", vm.Message);                              // Gaz still has a bill
        Assert.Equal(2, app.Core.Reader.GetPayees().Count);

        vm.Selected = vm.Items[1];
        vm.DeleteCommand.Execute(null);
        Assert.Single(app.Core.Reader.GetPayees());
        Assert.Contains("Woda", messages.Asked.Last());

        messages.Answer = false;                                                          // "no": nothing is deleted
        var before = app.Core.Reader.GetPayees().Count;
        vm.Selected = vm.Items[0];
        vm.DeleteCommand.Execute(null);
        Assert.Equal(before, app.Core.Reader.GetPayees().Count);

        var added = app.Core.Payments.AddPayee("Internet", Account);
        dialogs.PayeeAnswers.Enqueue(added);
        vm.AddCommand.Execute(null);
        Assert.Equal("Internet", vm.Selected!.Payee.Name);
        Assert.Equal(water.Id == Guid.Empty ? 0 : 2, vm.Items.Count);
    });

    // ----- typing the bills -----

    [Fact]
    public void PaymentEntry_EnterInTheAmountOpensTheNextRow_UntilAnEmptyOneIsLeft() => WithApp(app =>
    {
        var payee = AddPayee(app);
        var vm = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        Assert.Equal(payee, vm.SelectedPayee);                                            // the only payee is chosen at once
        Assert.Single(vm.Rows);

        var first = vm.Rows[0];
        first.DueDate = Today.AddDays(12).ToDateTime(TimeOnly.MinValue);
        first.Title = "Rata 1";
        first.AmountExpression = "100,50";
        var focus = vm.CommitRow(first);

        Assert.Equal(2, vm.Rows.Count);                                                   // a new row appeared
        Assert.Equal(PaymentFocus.Date, focus.Target);
        Assert.Same(vm.Rows[1], focus.Row);
        Assert.Equal(first.DueDate, vm.Rows[1].DueDate);                                  // it starts with the same date
        Assert.Equal("", vm.Rows[1].Title);

        var second = vm.Rows[1];
        second.DueDate = Today.AddDays(40).ToDateTime(TimeOnly.MinValue);
        second.Title = "Rata 2";
        second.AmountExpression = "2*50+0,25";                                             // a calculation works as in the entry window
        Assert.Equal(PaymentFocus.Date, vm.CommitRow(second).Target);
        Assert.Equal(100.25m, second.Value);
        Assert.Equal("100,25", second.Display);
        Assert.Equal(3, vm.Rows.Count);

        Assert.Equal(PaymentFocus.Confirm, vm.CommitRow(vm.Rows[2]).Target);              // an empty row: on to "confirm", no fourth row
        Assert.Equal(3, vm.Rows.Count);

        Assert.True(vm.Confirm());                                                        // the empty last row is not saved
        var list = app.Core.Reader.GetPlannedPayments();
        Assert.Equal(["Rata 1", "Rata 2"], list.Select(p => p.Title));
        Assert.Equal([100.50m, 100.25m], list.Select(p => p.Amount));
        Assert.All(list, p => Assert.Equal((payee.Id, PaymentStatus.Pending), (p.PayeeId, p.Status)));
    });

    [Fact]
    public void PaymentEntry_ChecksThePayeeAndEveryRow() => WithApp(app =>
    {
        var vm = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        Assert.False(vm.Confirm());
        Assert.Contains("odbiorcę", vm.ErrorMessage);

        AddPayee(app, "Pierwszy");
        AddPayee(app, "Drugi");
        var again = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        Assert.Null(again.SelectedPayee);                                                  // several payees: none chosen for the user
        again.SelectedPayee = again.Payees[0];
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", again.PayeeAccount);

        Assert.False(again.Confirm());
        Assert.Contains("przynajmniej jedną", again.ErrorMessage);

        again.Rows[0].Title = "Bez kwoty";
        Assert.False(again.Confirm());
        Assert.Contains("Wiersz 1", again.ErrorMessage);
        Assert.Equal(PaymentFocus.Amount, again.CommitRow(again.Rows[0]).Target);         // Enter on a missing amount stays there

        again.Rows[0].AmountExpression = "5+";
        Assert.True(again.Rows[0].IsInvalid);
        Assert.False(again.Confirm());

        again.Rows[0].AmountExpression = "5";
        again.Rows[0].DueDate = null;
        Assert.False(again.Confirm());
        Assert.Contains("termin", again.ErrorMessage);

        again.Rows[0].DueDate = DateTime.Today;
        Assert.True(again.Confirm());
        Assert.Equal(5m, Assert.Single(app.Core.Reader.GetPlannedPayments()).Amount);
    });

    [Fact]
    public void PaymentEntry_ANewPayeeCanBeAddedOnTheSpot_AndRowsCanBeRemoved() => WithApp(app =>
    {
        var dialogs = app.Get<FakePaymentDialogs>();
        var vm = new PaymentEntryViewModel(app.Core, dialogs, app.Get<ToastService>());
        Assert.Empty(vm.Payees);

        dialogs.PayeeAnswers.Enqueue(null);                                                // the payee window cancelled: nothing changes
        vm.NewPayeeCommand.Execute(null);
        Assert.Null(vm.SelectedPayee);

        dialogs.PayeeAnswers.Enqueue(app.Core.Payments.AddPayee("Nowy", Account));
        vm.NewPayeeCommand.Execute(null);
        Assert.Equal("Nowy", vm.SelectedPayee!.Name);
        Assert.Single(vm.Payees);

        vm.AddRowCommand.Execute(null);
        vm.AddRowCommand.Execute(null);
        Assert.Equal(3, vm.Rows.Count);
        vm.RemoveRowCommand.Execute(vm.Rows[1]);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal([1, 2], vm.Rows.Select(r => r.Number));
        vm.RemoveRowCommand.Execute(vm.Rows[0]);
        vm.RemoveRowCommand.Execute(vm.Rows[0]);                                           // the last row stays
        Assert.Single(vm.Rows);
    });

    // ----- the list and paying -----

    [Fact]
    public void Payments_ListShowsEveryBillWithItsStateAndSummary() => WithApp(app =>
    {
        var payee = AddPayee(app);
        AddBill(app, payee, -2, "Spóźniona", 50m);
        var soon = AddBill(app, payee, 3, "Za trzy dni", 70m);
        AddBill(app, payee, 20, "Daleko", 30m);
        app.Core.Payments.MarkScheduled(soon.Id);

        var vm = new PaymentsViewModel(app.Core, app.Get<DataHub>(), app.Get<FakePaymentDialogs>(), app.Get<FakeMessageDialogs>(), app.Get<ToastService>());
        Assert.Equal(["Spóźniona", "Za trzy dni", "Daleko"], vm.Items.Select(i => i.Title));        // by date
        Assert.True(vm.Items[0].IsLate);
        Assert.Equal("po terminie (2 dni)", vm.Items[0].Relative);
        Assert.Equal("termin za 3 dni", vm.Items[1].Relative);
        Assert.Equal("Zaplanowana", vm.Items[1].StatusText);
        Assert.Equal("Do zapłaty", vm.Items[2].StatusText);
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", vm.Items[0].Account);
        Assert.Contains("Do zapłaty: 2", vm.Summary);
        Assert.Contains("zaplanowanych w banku: 1", vm.Summary);
        Assert.True(vm.HasItems);

        var view = new PaymentsView { DataContext = vm };
        UiHost.Layout(view, 1150, 520);
        var texts = UiHost.Texts(view);
        UiHost.Render(view, 1150, 520, "payments");
        Assert.Contains("Spóźniona", texts);
        Assert.Contains("Elektrownia SA", texts);
    });

    [Fact]
    public void Payments_EachRowHasAPayButtonThatOpensTheQrWindow_AndTheListIsEmptyWhenThereAreNone() => WithApp(app =>
    {
        var dialogs = app.Get<FakePaymentDialogs>();
        var empty = new PaymentsViewModel(app.Core, app.Get<DataHub>(), dialogs, app.Get<FakeMessageDialogs>(), app.Get<ToastService>());
        Assert.False(empty.HasItems);
        Assert.Equal("", empty.Summary);

        var payee = AddPayee(app);
        var bill = AddBill(app, payee, 5);
        var vm = new PaymentsViewModel(app.Core, app.Get<DataHub>(), dialogs, app.Get<FakeMessageDialogs>(), app.Get<ToastService>());
        var view = new PaymentsView { DataContext = vm };
        UiHost.Layout(view, 1150, 400);
        Assert.Contains("Zapłać", UiHost.Descendants<Button>(view).Select(b => b.Content?.ToString()));

        vm.PayCommand.Execute(vm.Items[0]);
        Assert.Equal(bill.Id, Assert.Single(dialogs.Shown).Id);

        vm.AddPaymentsCommand.Execute(null);
        vm.ManagePayeesCommand.Execute(null);
        Assert.Equal((1, 1), (dialogs.EntriesOpened, dialogs.PayeeListsOpened));
    });

    [Fact]
    public void Payments_ABillCanBeRemovedFromTheListAfterAsking() => WithApp(app =>
    {
        var messages = app.Get<FakeMessageDialogs>();
        var payee = AddPayee(app);
        AddBill(app, payee, 5, "Do usunięcia", 12.34m);
        var vm = new PaymentsViewModel(app.Core, app.Get<DataHub>(), app.Get<FakePaymentDialogs>(), messages, app.Get<ToastService>());

        messages.Answer = false;
        vm.DeleteCommand.Execute(vm.Items[0]);
        Assert.Single(app.Core.Reader.GetPlannedPayments());
        Assert.Contains("Do usunięcia", messages.Asked.Last());

        messages.Answer = true;
        vm.DeleteCommand.Execute(vm.Items[0]);
        Assert.Empty(app.Core.Reader.GetPlannedPayments());
        Assert.Empty(vm.Items);
        Assert.Empty(app.Core.Reader.GetTransactions().Where(t => t.Note == "Do usunięcia"));     // removing is not paying
    });

    [Fact]
    public void Pay_TheQrWindowHoldsTheStandardTextAndAnImage() => WithApp(app =>
    {
        var category = app.Core.Reader.GetCategories().First(c => c.Name == "Rachunki").Id;
        var payee = AddPayee(app, "Elektrownia SA", category);
        var bill = AddBill(app, payee, 4, "Faktura FV/2026/10", 312.40m);
        var vm = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), bill);

        Assert.Equal($"|PL|{Account}|031240|Elektrownia SA|Faktura FV/2026/10|||", vm.QrText);
        Assert.Equal("", vm.QrNote);
        Assert.NotNull(vm.Qr);
        Assert.Equal(vm.Qr!.PixelWidth, vm.Qr.PixelHeight);                                // a square, with its quiet zone
        Assert.True(vm.Qr.PixelWidth >= 21 * 8);                                           // at least the smallest QR symbol at 8 px per module
        Assert.Equal("61 1090 1014 0000 0712 1981 2874", vm.AccountFormatted);
        Assert.Equal("termin za 4 dni", vm.Relative);

        var window = new PayWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 780, 470);
        var texts = UiHost.Texts(content);
        var buttons = UiHost.Descendants<Button>(content).Where(UiHost.IsShown).Select(b => b.Content?.ToString()).ToList();
        window.Content = null;
        content.DataContext = vm;
        UiHost.Render(content, 780, 470, "pay");

        Assert.Contains("Elektrownia SA", texts);
        Assert.Contains("Oznacz jako zapłaconą", buttons);
        Assert.Contains("Oznacz jako zaplanowaną", buttons);
        Assert.DoesNotContain("Wróć do „do zapłaty”", buttons);
    });

    [Fact]
    public void Pay_ALongTitleOrAHugeAmountIsReportedInTheNote() => WithApp(app =>
    {
        var payee = AddPayee(app, "Bardzo Długa Nazwa Odbiorcy Płatności");
        var longTitle = AddBill(app, payee, 4, "Bardzo długi tytuł płatności, który nie zmieści się w kodzie", 10m);
        Assert.Contains("skrócone", new PaymentPayViewModel(app.Core, app.Get<ToastService>(), longTitle).QrNote);

        var big = AddBill(app, payee, 4, "Kamienica", 25000m);
        var vm = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), big);
        Assert.Contains("nie mieści się", vm.QrNote);
        Assert.Equal("000000", vm.QrText.Split('|')[3]);
    });

    [Fact]
    public void Pay_MarkedPaid_BooksTodaysExpenseAndTakesTheBillOffTheList() => WithApp(app =>
    {
        var category = app.Core.Reader.GetCategories().First(c => c.Name == "Rachunki").Id;
        var payee = AddPayee(app, "Elektrownia SA", category);
        var bill = AddBill(app, payee, 4, "Faktura", 99.99m);
        var vm = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), bill);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.MarkPaidCommand.Execute(null);

        Assert.Equal((PayResult.Paid, 1), (vm.Result, closed));
        Assert.Empty(app.Core.Reader.GetPlannedPayments());
        var t = app.Core.Reader.GetTransactions().Single(x => x.Note == "Faktura");
        Assert.Equal((Today, 99.99m, category, "Elektrownia SA", TransactionType.Expense), (t.Date, t.Amount, t.CategoryId, t.Place, t.Type));   // today, not the due date
    });

    [Fact]
    public void Pay_MarkedScheduled_KeepsTheBillAndCanBeUndone() => WithApp(app =>
    {
        var payee = AddPayee(app);
        var bill = AddBill(app, payee, 6, "Czynsz", 800m);
        var vm = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), bill);

        vm.MarkScheduledCommand.Execute(null);
        Assert.Equal(PayResult.Scheduled, vm.Result);
        var stored = Assert.Single(app.Core.Reader.GetPlannedPayments());
        Assert.Equal(PaymentStatus.Scheduled, stored.Status);
        Assert.Empty(app.Core.Reader.GetTransactions().Where(t => t.Note == "Czynsz"));    // not booked yet

        // opened again, it offers the way back and the QR code is still there
        var again = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), stored);
        Assert.True(again.IsScheduled);
        Assert.NotNull(again.Qr);
        again.BackToPendingCommand.Execute(null);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(app.Core.Reader.GetPlannedPayments()).Status);
    });

    // ----- reminders -----

    private static PaymentReminderRunner Runner(TestApp app) => app.Get<PaymentReminderRunner>();

    [Fact]
    public void Reminders_AskThreeDaysBeforeTheDate_AndAtEveryStartUntilTheBillIsDealtWith() => WithApp(app =>
    {
        var prompt = app.Get<FakePaymentReminderPrompt>();
        var payee = AddPayee(app);
        AddBill(app, payee, 5, "Za pięć dni");
        AddBill(app, payee, 3, "Za trzy dni");
        AddBill(app, payee, -1, "Wczoraj");

        var first = Runner(app).Run(Today);
        Assert.Equal(["Wczoraj", "Za trzy dni"], prompt.Asked.Select(a => a.Title));       // "in five days" is not due yet
        Assert.Equal(2, first.Postponed);                                                  // both answered "later" by default
        Assert.Equal("po terminie (wczoraj)", prompt.Asked[0].Relative);
        Assert.True(prompt.Asked[0].IsLate);
        Assert.Equal("termin za 3 dni", prompt.Asked[1].Relative);

        // the same run of the program (the hourly check): not asked again
        prompt.Asked.Clear();
        Runner(app).Run(Today);
        Assert.Empty(prompt.Asked);

        // the next start of the program: a new runner, asked again
        var next = new PaymentReminderRunner(app.Core, prompt, app.Get<FakePaymentDialogs>());
        next.Run(Today);
        Assert.Equal(["Wczoraj", "Za trzy dni"], prompt.Asked.Select(a => a.Title));
    });

    [Fact]
    public void Reminders_PaidBooksTodayAndRemoves_PayNowOpensTheQr_LaterKeepsAsking() => WithApp(app =>
    {
        var prompt = app.Get<FakePaymentReminderPrompt>();
        var dialogs = app.Get<FakePaymentDialogs>();
        var payee = AddPayee(app);
        AddBill(app, payee, 0, "Dziś", 10m);
        var qr = AddBill(app, payee, 1, "Kod QR", 20m);
        AddBill(app, payee, 2, "Odłożona", 30m);
        prompt.Answers.Enqueue(ReminderAnswer.Paid);
        prompt.Answers.Enqueue(ReminderAnswer.PayNow);
        prompt.Answers.Enqueue(ReminderAnswer.Later);
        dialogs.PayAnswers.Enqueue(PayResult.Scheduled);                                    // in the QR window the user chose "scheduled"

        var outcome = Runner(app).Run(Today);

        Assert.Equal(1, outcome.Paid);
        var booked = app.Core.Reader.GetTransactions().Single(t => t.Note == "Dziś");
        Assert.Equal((Today, 10m), (booked.Date, booked.Amount));
        Assert.DoesNotContain(app.Core.Reader.GetPlannedPayments(), p => p.Title == "Dziś");

        Assert.Equal(qr.Id, Assert.Single(dialogs.Shown).Id);                               // "pay now" showed the code
        // the real QR window marks the bill as scheduled itself (the fake only answers); then it stays on the list without reminders
        app.Core.Payments.MarkScheduled(qr.Id);
        Assert.Equal(PaymentStatus.Scheduled, app.Core.Reader.GetPlannedPayments().Single(p => p.Title == "Kod QR").Status);
        Assert.Equal(PaymentStatus.Pending, app.Core.Reader.GetPlannedPayments().Single(p => p.Title == "Odłożona").Status);

        prompt.Asked.Clear();
        new PaymentReminderRunner(app.Core, prompt, dialogs).Run(Today);                    // the next start
        Assert.Equal(["Odłożona"], prompt.Asked.Select(a => a.Title));                      // paid and scheduled ones are not mentioned any more
    });

    [Fact]
    public void Reminders_ClosingTheQrWindowWithoutAChoiceMeansAskAgainLater() => WithApp(app =>
    {
        var prompt = app.Get<FakePaymentReminderPrompt>();
        var dialogs = app.Get<FakePaymentDialogs>();
        var payee = AddPayee(app);
        AddBill(app, payee, 0, "Dziś");
        prompt.Answers.Enqueue(ReminderAnswer.PayNow);
        dialogs.PayAnswers.Enqueue(PayResult.None);

        var outcome = Runner(app).Run(Today);
        Assert.Equal((0, 1), (outcome.Paid, outcome.Postponed));
        Assert.Single(app.Core.Reader.GetPlannedPayments());
        Assert.Equal(PaymentStatus.Pending, app.Core.Reader.GetPlannedPayments()[0].Status);
    });

    [Fact]
    public void Reminders_WaitWhileThereIsNoWindow_AndAskAgainOnRequest() => WithApp(app =>
    {
        var prompt = app.Get<FakePaymentReminderPrompt>();
        var payee = AddPayee(app);
        AddBill(app, payee, 1, "Jutro");

        prompt.CanAsk = false;
        var silent = Runner(app).Run(Today);
        Assert.Empty(prompt.Asked);
        Assert.Equal(1, silent.Waiting);

        prompt.CanAsk = true;
        prompt.Answers.Enqueue(ReminderAnswer.Later);
        Runner(app).Run(Today);
        Assert.Single(prompt.Asked);

        prompt.Asked.Clear();
        Runner(app).Run(Today);
        Assert.Empty(prompt.Asked);
        prompt.Answers.Enqueue(ReminderAnswer.Later);
        Runner(app).Run(Today, askAgain: true);
        Assert.Single(prompt.Asked);
    });

    [Fact]
    public void Reminders_ScheduledBillsNeverRemind_AndAreBookedOnTheirDateOncePassed() => WithApp(app =>
    {
        var prompt = app.Get<FakePaymentReminderPrompt>();
        var payee = AddPayee(app);
        var past = AddBill(app, payee, -4, "Przeszła", 400m);
        var tomorrow = AddBill(app, payee, 1, "Jutro", 410m);
        app.Core.Payments.MarkScheduled(past.Id);
        app.Core.Payments.MarkScheduled(tomorrow.Id);

        var outcome = Runner(app).Run(Today);

        Assert.Empty(prompt.Asked);                                                          // scheduled: no reminders at all
        Assert.Equal(1, outcome.Settled);
        var booked = Assert.Single(app.Core.Reader.GetTransactions(), t => t.Note == "Przeszła");
        Assert.Equal(Today.AddDays(-4), booked.Date);                                        // on the date that was planned
        Assert.Equal(["Jutro"], app.Core.Reader.GetPlannedPayments().Select(p => p.Title));  // the one for tomorrow waits
    });

    [Fact]
    public void Reminders_TheWindowShowsWhatToPayAndThreeButtons() => WithApp(app =>
    {
        var payee = AddPayee(app, "Elektrownia SA");
        var bill = AddBill(app, payee, 2, "Faktura 10/2026", 312.40m);
        var prompt = app.Get<FakePaymentReminderPrompt>();
        Runner(app).Run(Today);
        var reminder = Assert.Single(prompt.Asked);
        Assert.Equal(bill.Id, reminder.Payment.Id);

        var window = new PaymentReminderWindow(reminder);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 560, 360);
        var texts = UiHost.Texts(content);
        var buttons = UiHost.Descendants<Button>(content).Select(b => b.Content?.ToString()).ToList();
        window.Content = null;
        content.DataContext = reminder;
        UiHost.Render(content, 560, 360, "reminder");

        Assert.Contains("Przypomnij później", buttons);
        Assert.Contains("Zapłać teraz", buttons);
        Assert.Contains("Zapłacono", buttons);
        Assert.Contains("Elektrownia SA", texts);
        Assert.Contains("termin za 2 dni", texts);
        Assert.Contains("61 1090 1014 0000 0712 1981 2874", texts);
    });

    // ----- windows and navigation -----

    [Fact]
    public void Windows_ForTheEntryAndThePayeeBuildAndShowTheirFields() => WithApp(app =>
    {
        AddPayee(app);
        var entry = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        entry.Rows[0].Title = "Rata";
        entry.Rows[0].AmountExpression = "100";
        entry.CommitRow(entry.Rows[0]);

        var window = new PaymentEntryWindow(entry);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 760, 600);
        var texts = UiHost.Texts(content);
        window.Content = null;
        content.DataContext = entry;
        UiHost.Render(content, 760, 600, "payment-entry");
        Assert.Contains("Planowanie płatności", texts);
        Assert.Contains("Odbiorca", texts);
        Assert.Contains("Termin", texts);
        Assert.Contains("Zatwierdź", UiHost.Descendants<Button>(content).Select(b => b.Content?.ToString()));
        Assert.Equal(2, entry.Rows.Count);

        var payee = new PayeeEditViewModel(app.Core, null) { Name = "Tauron" };
        payee.AccountText = "PL61109010140000071219812874";
        var payeeWindow = new PayeeWindow(payee);
        var payeeContent = (FrameworkElement)payeeWindow.Content;
        UiHost.Layout(payeeContent, 560, 420);
        var payeeTexts = UiHost.Texts(payeeContent);
        payeeWindow.Content = null;
        payeeContent.DataContext = payee;
        UiHost.Render(payeeContent, 560, 420, "payee");
        Assert.Contains("Numer rachunku bankowego", payeeTexts);
        Assert.Contains(payeeTexts, t => t.Contains("poprawny"));

        var list = new PayeesWindow(new PayeesViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<FakeMessageDialogs>()));
        UiHost.Layout((FrameworkElement)list.Content, 640, 460);
        Assert.Contains("Elektrownia SA", UiHost.Texts((FrameworkElement)list.Content));
    });

    [Fact]
    public void Navigation_HasAPaymentsPage_WithItsOwnShortcut() => WithApp(app =>
    {
        var main = app.Get<MainViewModel>();
        Assert.Contains(main.NavItems, n => n.Key == "payments" && n.Title == "Płatności");
        Assert.Equal("Ctrl + 6", main.NavItems.First(n => n.Key == "payments").Shortcut);
        Assert.Equal("Ctrl + 7", main.NavItems.First(n => n.Key == "reports").Shortcut);
        main.Navigate("payments");
        Assert.IsType<PaymentsViewModel>(main.CurrentPage);
        Assert.Contains("payments", ShortcutMap.Pages);

        var settings = app.Get<SettingsViewModel>();
        Assert.Contains(settings.StartPages, p => p.Key == "payments" && p.Label == "Płatności");     // it can be the page shown at startup
    });
}
