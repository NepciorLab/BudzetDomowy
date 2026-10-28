using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>An entry that is already there: the user is asked, and decides to add, to give up or to join the entries.</summary>
public class DuplicateUiTests
{
    private sealed class FakePrompt(DuplicateChoice answer) : IDuplicatePrompt
    {
        public List<IReadOnlyList<DuplicateInfo>> Asked { get; } = [];

        public DuplicateChoice Ask(IReadOnlyList<DuplicateInfo> existing)
        {
            Asked.Add(existing);
            return answer;
        }
    }

    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static Guid Cat(TestApp app, string name) => app.Core.Reader.GetCategories().First(c => c.Name == name).Id;

    private static Transaction Existing(TestApp app, decimal amount, string category, string place = "ZzDup") =>
        app.Core.Transactions.Add(Today, TransactionType.Expense, amount, Cat(app, category), "", null, place: place);

    private static int Count(TestApp app) => app.Core.Reader.GetTransactions().Count;

    private static EntryViewModel Entry(TestApp app, IDuplicatePrompt prompt, string place, params (string Category, string Amount)[] lines)
    {
        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: false, prompt);
        vm.Place = place;
        vm.TotalText = lines.Sum(l => decimal.Parse(l.Amount)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        vm.Lines[0].AmountExpression = lines[0].Amount;
        vm.Lines[0].CategoryId = Cat(app, lines[0].Category);
        for (var i = 1; i < lines.Length; i++)
        {
            vm.CommitLine(vm.Lines[i - 1]);                                         // opens the next line with what is left
            vm.Lines[i].AmountExpression = lines[i].Amount;
            vm.Lines[i].CategoryId = Cat(app, lines[i].Category);
        }
        return vm;
    }

    [Fact]
    public void NewEntry_WithoutATwin_IsSavedWithoutAsking() => WithApp(app =>
    {
        Existing(app, 100m, "Spożywcze");
        var prompt = new FakePrompt(DuplicateChoice.Cancel);
        var before = Count(app);

        Assert.True(Entry(app, prompt, "ZzDup", ("Spożywcze", "101")).Confirm());         // another amount
        Assert.True(Entry(app, prompt, "ZzInne", ("Spożywcze", "100")).Confirm());        // another place
        Assert.True(Entry(app, prompt, "ZzDup", ("Auto", "100")).Confirm());              // another category

        Assert.Empty(prompt.Asked);
        Assert.Equal(before + 3, Count(app));
    });

    [Fact]
    public void NewEntry_WithATwin_AsksAndTellsWhatIsThere_AndAddAddsIt() => WithApp(app =>
    {
        var twin = Existing(app, 100m, "Spożywcze");
        var prompt = new FakePrompt(DuplicateChoice.Add);
        var before = Count(app);

        Assert.True(Entry(app, prompt, "zzdup", ("Spożywcze", "100")).Confirm());          // the place in other letters is the same place

        var asked = Assert.Single(Assert.Single(prompt.Asked));
        Assert.Equal((Today, "ZzDup", "Spożywcze", 100m), (asked.Date, asked.Place, asked.Category, asked.Amount));
        Assert.Equal(before + 1, Count(app));                                              // both are there now
        Assert.Equal(100m, app.Core.Reader.GetTransactions().Single(t => t.Id == twin.Id).Amount);
    });

    [Fact]
    public void NewEntry_WithATwin_GiveUpSavesNothing_AndLeavesTheWindowForChanges() => WithApp(app =>
    {
        Existing(app, 100m, "Spożywcze");
        var prompt = new FakePrompt(DuplicateChoice.Cancel);
        var before = Count(app);
        var saved = false;
        var vm = Entry(app, prompt, "ZzDup", ("Spożywcze", "100"));
        vm.Saved += () => saved = true;

        Assert.False(vm.Confirm());

        Assert.False(saved);                                                              // the window is not closed
        Assert.Equal("", vm.ErrorMessage);                                                // and nothing is wrong with the entry
        Assert.Equal(before, Count(app));
        vm.TotalText = "101";                                                             // changed: now it is a different entry
        vm.Lines[0].AmountExpression = "101";
        Assert.True(vm.Confirm());
        Assert.Equal(before + 1, Count(app));
        Assert.Single(prompt.Asked);
    });

    [Fact]
    public void NewEntry_WithATwin_JoinGrowsTheExistingEntry_SavesNoNewOne_AndUndoRestoresIt() => WithApp(app =>
    {
        var twin = Existing(app, 100m, "Spożywcze");
        var prompt = new FakePrompt(DuplicateChoice.Merge);
        var before = Count(app);

        Assert.True(Entry(app, prompt, "ZzDup", ("Spożywcze", "100")).Confirm());

        Assert.Equal(before, Count(app));                                                 // no second entry
        var joined = app.Core.Reader.GetTransactions().Single(t => t.Id == twin.Id);
        Assert.Equal((200m, 200m), (joined.Amount, joined.AmountPln));

        Assert.True(app.Get<ToastService>().TryUndoLast());                               // the toast takes it back
        Assert.Equal(100m, app.Core.Reader.GetTransactions().Single(t => t.Id == twin.Id).Amount);
        Assert.Equal(before, Count(app));
    });

    [Fact]
    public void NewEntryWithSeveralLines_EachLineIsCompared_AndOnlyTheOnesWithATwinAreJoined() => WithApp(app =>
    {
        var first = Existing(app, 60m, "Spożywcze");
        var second = Existing(app, 40m, "Cukiernicze");
        var before = Count(app);

        // 60 groceries (has a twin), 15 car (new), 40 sweets (has a twin)
        var prompt = new FakePrompt(DuplicateChoice.Merge);
        Assert.True(Entry(app, prompt, "ZzDup", ("Spożywcze", "60"), ("Auto", "15"), ("Cukiernicze", "40")).Confirm());

        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(["Spożywcze", "Cukiernicze"], asked.Select(a => a.Category));          // both twins are shown, the new line is not
        var all = app.Core.Reader.GetTransactions();
        Assert.Equal((120m, 80m), (all.Single(t => t.Id == first.Id).Amount, all.Single(t => t.Id == second.Id).Amount));
        Assert.Equal(before + 1, Count(app));                                              // only the car line is a new entry
        Assert.Equal(15m, all.Single(t => t.Place == "ZzDup" && t.CategoryId == Cat(app, "Auto")).Amount);

        Assert.True(app.Get<ToastService>().TryUndoLast());                                // one undo takes back all of it
        all = app.Core.Reader.GetTransactions();
        Assert.Equal((60m, 40m), (all.Single(t => t.Id == first.Id).Amount, all.Single(t => t.Id == second.Id).Amount));
        Assert.Equal(before, Count(app));
    });

    [Fact]
    public void NewEntryWithSeveralLines_AddSavesAllOfThem() => WithApp(app =>
    {
        Existing(app, 60m, "Spożywcze");
        var before = Count(app);
        var prompt = new FakePrompt(DuplicateChoice.Add);
        Assert.True(Entry(app, prompt, "ZzDup", ("Spożywcze", "60"), ("Auto", "15")).Confirm());
        Assert.Equal(before + 2, Count(app));
        Assert.Single(Assert.Single(prompt.Asked));
    });

    /// <summary>Opens the real question window, lets <paramref name="act"/> look at it and press a button, and returns the answer.</summary>
    private static DuplicateChoice AskInWindow(IReadOnlyList<DuplicateInfo> existing, Action<System.Windows.Window> act)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            var window = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsVisible && w.Title == "Taki wpis już jest");
            if (window is null) return;
            timer.Stop();
            act(window);
        };
        timer.Start();
        var answer = new WpfDuplicatePrompt().Ask(existing);
        timer.Stop();
        return answer;
    }

    private static void Press(System.Windows.Window window, string text) =>
        UiHost.Descendants<System.Windows.Controls.Button>(window).First(b => (b.Content as string) == text)
            .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Fact]
    public void TheQuestionWindow_HasTheThreeAnswers_ShowsWhatIsThere_AndGivesUpOnEsc() => WithApp(app =>
    {
        var info = new[] { new DuplicateInfo(new DateOnly(2026, 10, 28), "Lidl", "Spożywcze", 100m, "PLN"), new DuplicateInfo(new DateOnly(2026, 10, 28), "Lidl", "Auto", 12.5m, "PLN") };
        List<string> texts = [];
        List<string?> buttons = [];

        var merge = AskInWindow(info, w =>
        {
            texts = UiHost.Texts(w);
            buttons = UiHost.Descendants<System.Windows.Controls.Button>(w).Select(b => b.Content as string).Where(c => c is not null && c.Length < 20).ToList();
            Press(w, "Połącz wpisy");
        });

        Assert.Equal(DuplicateChoice.Merge, merge);
        Assert.Equal(["Dodaj", "Zrezygnuj", "Połącz wpisy"], buttons);
        Assert.Contains(texts, t => t.Contains("Lidl") && t.Contains("Spożywcze") && t.Contains("100,00 PLN"));
        Assert.Contains(texts, t => t.Contains("Auto") && t.Contains("12,50 PLN"));
        Assert.Contains(texts, t => t.Contains("Wpisy"));                                  // several: the plural question

        Assert.Equal(DuplicateChoice.Add, AskInWindow(info.Take(1).ToList(), w => Press(w, "Dodaj")));
        Assert.Equal(DuplicateChoice.Cancel, AskInWindow(info.Take(1).ToList(), w =>
        {
            Assert.Contains(UiHost.Texts(w), t => t.StartsWith("Wpis z tego"));            // one: the singular question
            Press(w, "Zrezygnuj");
        }));
    });

    [Fact]
    public void AnIncome_IsComparedToo() => WithApp(app =>
    {
        app.Core.Transactions.Add(Today, TransactionType.Income, 500m, Cat(app, "Wynagrodzenie"), "", null);
        var prompt = new FakePrompt(DuplicateChoice.Cancel);
        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: true, prompt);
        vm.TotalText = "500";
        vm.Lines[0].CategoryId = Cat(app, "Wynagrodzenie");
        var before = Count(app);
        Assert.False(vm.Confirm());
        Assert.Single(prompt.Asked);
        Assert.Equal(before, Count(app));
    });
}
