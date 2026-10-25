using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>The new entry window: totals split over categories, calculations, "r", learned categories, W and Q.</summary>
public class V5UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static Guid Cat(TestApp app, string name) => app.Core.Reader.GetCategories().First(c => c.Name == name).Id;

    private static EntryViewModel NewEntry(TestApp app, bool income = false) => new(app.Core, app.Get<ToastService>(), income);

    /// <summary>History for the place "Sklep": groceries three times, sweets twice, car once.</summary>
    private static void SeedShop(TestApp app)
    {
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(-20);
        foreach (var name in new[] { "Spożywcze", "Spożywcze", "Spożywcze", "Cukiernicze", "Cukiernicze", "Auto" })
            app.Core.Transactions.Add(day, TransactionType.Expense, 10m, Cat(app, name), "", null, place: "Sklep");
    }

    private static decimal AmountOf(Transaction t) => t.AmountPln;

    // ----- one item -----

    [Fact]
    public void Entry_ASingleItemTakesTheWholeTotalAndTheCategoryOfThePlace() => WithApp(app =>
    {
        var vm = NewEntry(app);
        var before = app.Core.Reader.GetTransactions().Count;

        vm.Place = "Lewiatan";
        vm.TotalText = "100";

        var line = Assert.Single(vm.Lines);
        Assert.Equal("100", line.AmountExpression);
        Assert.True(line.IsAuto);
        Assert.Equal(Cat(app, "Spożywcze"), line.CategoryId);                       // the history of the place decides
        Assert.Equal("Rozdysponowano 100 z 100, zostało 0", vm.Summary);

        Assert.Equal(EntryFocusTarget.Confirm, vm.CommitLine(line).Target);        // nothing left: on to "confirm"
        Assert.True(vm.Confirm());

        var saved = app.Core.Reader.GetTransactions().Count - before;
        Assert.Equal(1, saved);
        var t = app.Core.Reader.GetTransactions().Single(x => x.Amount == 100m);
        Assert.Equal((Cat(app, "Spożywcze"), "Lewiatan", TransactionType.Expense), (t.CategoryId, t.Place, t.Type));
    });

    [Fact]
    public void Entry_TheTotalFollowsWhatIsTypedUntilTheLineIsEdited() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "40";
        Assert.Equal("40", vm.Lines[0].AmountExpression);
        vm.TotalText = "55,5";
        Assert.Equal("55,5", vm.Lines[0].AmountExpression);
        Assert.Equal(55.5m, vm.Lines[0].Value);

        vm.Lines[0].AmountExpression = "20";                                          // typed by hand: no longer follows
        vm.TotalText = "70";
        Assert.Equal("20", vm.Lines[0].AmountExpression);
    });

    // ----- several items -----

    [Fact]
    public void Entry_ALowerAmountOpensTheNextLineWithTheRestAndTheNextCategory() => WithApp(app =>
    {
        SeedShop(app);
        var vm = NewEntry(app);
        vm.Place = "sklep";
        vm.TotalText = "100";
        Assert.Equal(Cat(app, "Spożywcze"), vm.Lines[0].CategoryId);                 // the most frequent

        var first = vm.Lines[0];
        first.AmountExpression = "60";
        var focus = vm.CommitLine(first);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal(EntryFocusTarget.LineCategory, focus.Target);
        var second = vm.Lines[1];
        Assert.Same(second, focus.Line);
        Assert.Equal("40", second.AmountExpression);                                  // what is left, filled in
        Assert.Equal(Cat(app, "Cukiernicze"), second.CategoryId);                    // the first suggestion is taken: the second one

        // the rest accepted as it is: nothing left, on to "confirm"
        Assert.Equal(EntryFocusTarget.Confirm, vm.CommitLine(second).Target);

        var before = app.Core.Reader.GetTransactions().Count;
        Assert.True(vm.Confirm());
        var added = app.Core.Reader.GetTransactions().Skip(0).Where(t => t.Place == "sklep").ToList();
        Assert.Equal(2, added.Count);
        Assert.Equal(before + 2, app.Core.Reader.GetTransactions().Count);
        Assert.Equal([60m, 40m], added.OrderByDescending(AmountOf).Select(AmountOf));
        Assert.Equal(Cat(app, "Cukiernicze"), added.Single(t => t.Amount == 40m).CategoryId);
    });

    [Fact]
    public void Entry_WhenTheFirstSuggestionWasChanged_TheNextLineStillGetsTheMostFrequentOne() => WithApp(app =>
    {
        SeedShop(app);
        var vm = NewEntry(app);
        vm.Place = "Sklep";
        vm.TotalText = "100";
        vm.Lines[0].CategoryId = Cat(app, "Auto");                                    // the user chose another category

        vm.Lines[0].AmountExpression = "30";
        vm.CommitLine(vm.Lines[0]);

        Assert.Equal(Cat(app, "Spożywcze"), vm.Lines[1].CategoryId);                 // the top one is still free
        vm.Lines[1].AmountExpression = "30";
        vm.CommitLine(vm.Lines[1]);
        Assert.Equal(Cat(app, "Cukiernicze"), vm.Lines[2].CategoryId);               // and then the next most frequent
        Assert.Equal("40", vm.Lines[2].AmountExpression);

        vm.Lines[2].AmountExpression = "10";
        vm.CommitLine(vm.Lines[2]);
        Assert.Equal(4, vm.Lines.Count);
        Assert.Null(vm.Lines[3].CategoryId);                                          // nothing left to suggest
        Assert.Equal("30", vm.Lines[3].AmountExpression);
    });

    [Fact]
    public void Entry_ALineWithRTakesWhatIsLeft() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "100";
        vm.Lines[0].AmountExpression = "30";
        var second = vm.AddLine();
        second.CategoryId = Cat(app, "Auto");
        second.AmountExpression = "r";

        Assert.True(second.IsRemainder);
        Assert.Equal(70m, second.Value);
        Assert.Equal("70", second.Display);

        vm.Lines[0].AmountExpression = "45";                                           // the remainder moves with the other lines
        Assert.Equal(55m, second.Value);
        Assert.Equal(EntryFocusTarget.Confirm, vm.CommitLine(second).Target);

        Assert.True(vm.Confirm());
        var amounts = app.Core.Reader.GetTransactions().Where(t => t.Amount is 45m or 55m).Select(AmountOf).Order().ToList();
        Assert.Equal([45m, 55m], amounts);
    });

    [Fact]
    public void Entry_OnlyOneLineMayAskForTheRemainder() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "100";
        vm.Lines[0].AmountExpression = "r";
        var second = vm.AddLine();
        second.AmountExpression = "R";

        Assert.False(vm.Lines[0].IsInvalid);
        Assert.True(second.IsInvalid);
        Assert.False(vm.Confirm());
        Assert.Contains("Tylko jedna pozycja", vm.ErrorMessage);
    });

    [Fact]
    public void Entry_CalculationsInAnAmountAreEvaluated_AndOnlyTheResultIsSaved() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "10*10+5";
        Assert.Equal(105m, vm.Total);
        Assert.Equal("105", vm.TotalDisplay);                                          // shown after Enter

        var line = vm.Lines[0];
        Assert.Equal("105", line.AmountExpression);
        line.AmountExpression = "2+2*2";
        Assert.Equal(6m, line.Value);
        Assert.Equal("6", line.Display);                                               // the box shows the result …
        Assert.Equal("2+2*2", line.AmountExpression);                                  // … and still holds the calculation for when it is entered again

        vm.TotalText = "6";
        Assert.Equal("", vm.TotalDisplay);                                             // a plain number shows as typed
        vm.Lines[0].AmountExpression = "7,5";
        Assert.Equal("", vm.Lines[0].Display);

        vm.Lines[0].AmountExpression = "2+2*2";
        var before = app.Core.Reader.GetTransactions().Count;
        Assert.True(vm.Confirm());
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);
        Assert.Equal(6m, app.Core.Reader.GetTransactions().Last(t => t.Amount == 6m).Amount);   // just the result, no trace of the calculation
        Assert.DoesNotContain(app.Core.Reader.GetTransactions(), t => t.Note.Contains("2+2"));
    });

    // ----- checks -----

    [Fact]
    public void Entry_AnAmountOverTheTotalAndABrokenOneAreRefused() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "50";
        var line = vm.Lines[0];

        line.AmountExpression = "70";
        var over = vm.CommitLine(line);
        Assert.Equal(EntryFocusTarget.LineAmount, over.Target);
        Assert.Contains("przekracza", vm.ErrorMessage);

        line.AmountExpression = "2++";
        Assert.True(line.IsInvalid);
        Assert.Equal(EntryFocusTarget.LineAmount, vm.CommitLine(line).Target);
        Assert.Contains("Pozycja 1", vm.ErrorMessage);

        var empty = NewEntry(app);
        Assert.Equal(EntryFocusTarget.Total, empty.CommitLine(empty.Lines[0]).Target);   // no total yet: back to it
        Assert.Contains("kwotę łączną", empty.ErrorMessage);
    });

    [Fact]
    public void Entry_TheItemsMustAddUpToTheTotal_AndNothingIsSavedOtherwise() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "100";
        vm.Lines[0].AmountExpression = "60";
        var before = app.Core.Reader.GetTransactions().Count;

        Assert.False(vm.Confirm());
        Assert.Contains("mniejsza", vm.ErrorMessage);
        Assert.Equal(before, app.Core.Reader.GetTransactions().Count);

        Assert.False(NewEntry(app).Confirm());                                          // no total at all
        var broken = NewEntry(app);
        broken.TotalText = "abc";
        Assert.True(broken.TotalInvalid);
        Assert.False(broken.Confirm());
        Assert.Equal(before, app.Core.Reader.GetTransactions().Count);
    });

    [Fact]
    public void Entry_ThePlusButtonAddsAnItem_AndAnEmptyOneIsIgnoredOnSaving() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "20";
        vm.AddEmptyLineCommand.Execute(null);                                           // nothing is left to assign: an empty line
        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal("", vm.Lines[1].AmountExpression);
        Assert.True(vm.Lines[1].CanRemove);

        Assert.True(vm.Confirm());                                                      // the empty line is not saved
        Assert.Equal(1, app.Core.Reader.GetTransactions().Count(t => t.Amount == 20m));

        var another = NewEntry(app);
        another.TotalText = "20";
        another.Lines[0].AmountExpression = "5";
        another.AddEmptyLineCommand.Execute(null);
        Assert.Equal("15", another.Lines[1].AmountExpression);                          // the rest is offered
        another.RemoveCommand.Execute(another.Lines[1]);
        Assert.Single(another.Lines);
        another.RemoveCommand.Execute(another.Lines[0]);                                // the last line cannot be removed
        Assert.Single(another.Lines);
    });

    // ----- income, currency, event -----

    [Fact]
    public void Entry_AnIncomeUsesIncomeCategoriesAndItsOwnHistory() => WithApp(app =>
    {
        var vm = NewEntry(app, income: true);
        Assert.Equal("Nowy przychód", vm.Title);
        Assert.All(vm.Categories, c => Assert.NotEqual(CategoryType.Expense, c.Category.Type));

        vm.Place = "Firma";                                                              // booked as income before
        vm.TotalText = "1000";
        Assert.Equal(Cat(app, "Wynagrodzenie"), vm.Lines[0].CategoryId);
        Assert.True(vm.Confirm());
        Assert.Contains(app.Core.Reader.GetTransactions(), t => t.Type == TransactionType.Income && t.Amount == 1000m && t.Place == "Firma");

        var expense = NewEntry(app);
        expense.Place = "Firma";
        Assert.Null(expense.Lines[0].CategoryId);                                        // an expense window knows nothing of income places
    });

    [Fact]
    public void Entry_ACurrencyTypedAfterTheTotalSwitchesTheCurrency() => WithApp(app =>
    {
        var vm = NewEntry(app);
        vm.TotalText = "12,50 EUR";
        Assert.Equal("EUR", vm.CurrencyCode);
        Assert.True(vm.IsForeign);
        Assert.Equal(12.5m, vm.Total);

        vm.TotalText = "2*6 usd";
        Assert.Equal("USD", vm.CurrencyCode);
        Assert.Equal(12m, vm.Total);
        Assert.Equal("12 USD", vm.TotalDisplay);
    });

    [Fact]
    public void Entry_ABookedEventAndNoteGoToEveryItem() => WithApp(app =>
    {
        var vm = NewEntry(app);
        var holiday = app.Core.Reader.GetSpecialEvents().First();
        vm.EventKey = holiday.Id;
        vm.Note = "paragon";
        vm.Place = "Kiosk";
        vm.TotalText = "30";
        vm.Lines[0].AmountExpression = "10";
        vm.CommitLine(vm.Lines[0]);
        Assert.True(vm.Confirm());

        var saved = app.Core.Reader.GetTransactions().Where(t => t.Place == "Kiosk").ToList();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, t => Assert.Equal((holiday.Id, "paragon"), (t.EventId, t.Note)));
    });

    [Fact]
    public void Entry_TheSavedReceiptCanBeUndoneInOneStep() => WithApp(app =>
    {
        var toast = app.Get<ToastService>();
        var vm = NewEntry(app);
        vm.Place = "Kiosk";
        vm.TotalText = "30";
        vm.Lines[0].AmountExpression = "10";
        vm.CommitLine(vm.Lines[0]);
        var before = app.Core.Reader.GetTransactions().Count;
        Assert.True(vm.Confirm());
        Assert.Equal(before + 2, app.Core.Reader.GetTransactions().Count);

        Assert.True(toast.TryUndoLast());
        Assert.Equal(before, app.Core.Reader.GetTransactions().Count);                  // both items disappear together
    });

    // ----- the window and the keys -----

    [Fact]
    public void EntryWindow_ShowsTheReceiptFieldsAndTheLines() => WithApp(app =>
    {
        SeedShop(app);
        var vm = NewEntry(app);
        vm.Place = "Sklep";
        vm.TotalText = "100";
        vm.Lines[0].AmountExpression = "60";
        vm.CommitLine(vm.Lines[0]);

        var window = new EntryWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 780, 680);
        var texts = UiHost.Texts(content);
        var buttons = UiHost.Descendants<Button>(content).Select(b => b.Content?.ToString()).ToList();
        window.Content = null;
        content.DataContext = vm;
        UiHost.Render(content, 780, 680, "entry");

        Assert.Contains("Nowy wydatek", texts);
        Assert.Contains("Zatwierdź", buttons);
        Assert.Contains(texts, t => t.StartsWith("Rozdysponowano 100 z 100"));      // the second line already holds the rest
        Assert.Equal(2, vm.Lines.Count);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void ExpressionBox_ShowsTheCalculationWhileEditedAndTheResultAfterwards() => WithApp(app =>
    {
        var box = new ExpressionBox { Expression = "2+2*2", DisplayText = "6" };
        Assert.Equal("6", box.Text);                                                     // not being edited: the result

        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, box) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        Assert.Equal("2+2*2", box.Text);                                                 // back in the box: the calculation again

        box.Text = "3*3";
        box.Commit();
        Assert.Equal("3*3", box.Expression);                                             // handed over to its owner …
        box.DisplayText = "9";
        Assert.Equal("9", box.Text);                                                     // … and replaced by the result
    });

    private static void PressKey(MainWindow window, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        typeof(MainWindow).GetMethod("OnEntryKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, e]);
        Assert.True(e.Handled || key is not (Key.W or Key.Q));
    }

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    [Fact]
    public void MainWindow_WAndQOpenTheEntryWindows_AndTheTopShowsWhatToPress() => WithApp(app =>
    {
        var launcher = app.Get<FakeEntryLauncher>();
        var main = app.Get<MainViewModel>();
        var window = new MainWindow(main, app.Get<SettingsStore>());

        Assert.Equal("W – nowy wydatek", main.ExpenseHint);
        Assert.Equal("Q – nowy przychód", main.IncomeHint);
        Assert.Equal("W – nowy wydatek", Assert.IsType<Button>(window.FindName("NewExpenseButton")).Content);
        Assert.Null(window.FindName("QuickBar"));                                         // the entry fields are gone

        PressKey(window, Key.W);
        PressKey(window, Key.Q);
        PressKey(window, Key.X);                                                          // any other key does nothing
        Assert.Equal([false, true], launcher.Opened);

        main.NewExpenseCommand.Execute(null);                                             // the buttons do the same
        main.NewIncomeCommand.Execute(null);
        Assert.Equal([false, true, false, true], launcher.Opened);
    });

    [Fact]
    public void Shortcuts_TheKeysForNewEntriesCanBeChangedToOtherLetters() => WithApp(app =>
    {
        var map = ShortcutMap.Instance;
        map.Initialize(app.Get<SettingsStore>());
        try
        {
            Assert.Equal("W", map.KeysOf(ShortcutMap.NewExpense));
            Assert.Equal("Q", map.KeysOf(ShortcutMap.NewIncome));
            Assert.Null(map.Set(ShortcutMap.NewExpense, Key.E, ModifierKeys.None));        // a plain letter is fine here …
            Assert.NotNull(map.Set(ShortcutMap.Help, Key.H, ModifierKeys.None));           // … but not for the other commands
            Assert.NotNull(map.Set(ShortcutMap.NewIncome, Key.E, ModifierKeys.None));      // and not twice

            var main = app.Get<MainViewModel>();
            Assert.Equal("E – nowy wydatek", main.ExpenseHint);                            // the hint follows the key
            var window = new MainWindow(main, app.Get<SettingsStore>());
            PressKey(window, Key.E);
            Assert.Equal([false], app.Get<FakeEntryLauncher>().Opened);
        }
        finally
        {
            foreach (var d in ShortcutMap.Definitions) map.Reset(d.Id);
        }
    });
}
