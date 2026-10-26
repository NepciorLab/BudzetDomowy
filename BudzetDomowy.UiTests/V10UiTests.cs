using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>The day range of the transactions, the suggestions of the place box and the moment recurring entries are asked about.</summary>
public class V10UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static void WithAppAsync(Func<TestApp, Task> test)
    {
        using var app = new TestApp();
        UiHost.RunAsync(() => test(app));
    }

    // ----- transactions: day and week -----

    [Fact]
    public void Transactions_DayMode_ShowsOneDayWithArrows_AndTheWeekTitleHasNoWeekNumber() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.Anchor = new DateOnly(2026, 10, 26);                       // a Monday

        vm.SetModeCommand.Execute("Day");
        Assert.True(vm.IsDayMode);
        Assert.True(vm.HasArrows);
        Assert.Equal("Poniedziałek, 26 października 2026", vm.PeriodTitle);
        Assert.All(vm.Rows, r => Assert.Equal(new DateOnly(2026, 10, 26), r.Model.Date));

        vm.NextPeriod();
        Assert.Equal(new DateOnly(2026, 10, 27), vm.Anchor);
        vm.PreviousPeriod();
        vm.PreviousPeriod();
        Assert.Equal(new DateOnly(2026, 10, 25), vm.Anchor);

        vm.SetModeCommand.Execute("Week");
        Assert.True(vm.IsWeekMode);
        Assert.False(vm.IsDayMode);
        Assert.Equal("19.10 – 25.10.2026", vm.PeriodTitle);
        Assert.DoesNotContain("ydzie", vm.PeriodTitle);
    });

    // ----- the place box: suggestions and their editing in the settings -----

    [Fact]
    public void EntryWindow_OffersThePlacesTypedBefore_BestFirst() => WithApp(app =>
    {
        foreach (var place in new[] { "Zzlewiatan", "Zzlewiatan", "Zzlidl" })
            app.Core.Transactions.Add(new DateOnly(2026, 10, 1), TransactionType.Expense, 10m, null, "", null, place: place);

        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: false);
        Assert.Equal(["Zzlewiatan", "Zzlidl"], vm.PlaceSuggestions.Select(s => s.Name).Where(n => n.StartsWith("Zz")));
    });

    [Fact]
    public void SettingsPlaces_AddRemoveAndChangeTheWeight() => WithApp(app =>
    {
        foreach (var place in new[] { "Zzlewiatan", "Zzlewiatan", "Zzlidl" })
            app.Core.Transactions.Add(new DateOnly(2026, 10, 1), TransactionType.Expense, 10m, null, "", null, place: place);
        var vm = app.Get<SettingsViewModel>().PlaceHints;
        vm.Search = "zz";                                             // the sample data has other places too
        Assert.Equal(["Zzlewiatan", "Zzlidl"], vm.Rows.Select(r => r.Name));
        Assert.Equal([2, 1], vm.Rows.Select(r => r.Weight));

        vm.Rows[1].Weight = 10;                                       // typed into the table
        Assert.Equal(["Zzlidl", "Zzlewiatan"], app.Core.PlaceSuggestions.All().Select(s => s.Name).Where(n => n.StartsWith("Zz")));
        Assert.True(vm.Rows[1].CanReset);

        vm.NewName = "Zzadaś";
        vm.NewWeight = "5";
        vm.AddCommand.Execute(null);
        Assert.Equal(["Zzlidl", "Zzadaś", "Zzlewiatan"], vm.Rows.Select(r => r.Name));
        Assert.Equal("", vm.NewName);

        vm.NewName = "Zły";
        vm.NewWeight = "abc";
        vm.AddCommand.Execute(null);
        Assert.Equal(Res.Get("PlacesBadWeight"), vm.Message);

        vm.RemoveCommand.Execute(vm.Rows.First(r => r.Name == "Zzlewiatan"));
        Assert.Equal(["Zzlidl", "Zzadaś"], vm.Rows.Select(r => r.Name));

        vm.ResetWeightCommand.Execute(vm.Rows.First(r => r.Name == "Zzlidl"));
        Assert.Equal(1, app.Core.PlaceSuggestions.All().First(s => s.Name == "Zzlidl").Weight);

        vm.Search = "zzad";
        Assert.Equal(["Zzadaś"], vm.Rows.Select(r => r.Name));
    });

    [Fact]
    public void SettingsPlaces_ImportWithAMinimum_AndRemovingManyAtOnce() => WithApp(app =>
    {
        foreach (var place in new[] { "Zzaaa", "Zzaaa", "Zzaaa", "Zzbbb", "Zzbbb", "Zzccc" })
            app.Core.Transactions.Add(new DateOnly(2026, 10, 1), TransactionType.Expense, 10m, null, "", null, place: place);
        var vm = app.Get<SettingsViewModel>().PlaceHints;

        vm.MinEntries = "0";
        vm.ImportCommand.Execute(null);
        Assert.Equal(Res.Get("PlacesBadMin"), vm.Message);

        vm.MinEntries = "2";
        vm.ImportCommand.Execute(null);
        vm.Search = "zz";
        Assert.Equal(["Zzaaa", "Zzbbb"], vm.Rows.Select(r => r.Name));        // Zzccc was typed once only

        vm.RemoveMany(vm.Rows.Where(r => r.Name != "Zzaaa").ToList());
        Assert.Equal(["Zzaaa"], vm.Rows.Select(r => r.Name));
        Assert.Equal(Res.Format("PlacesRemoved", "Zzbbb"), vm.Message);

        vm.MinEntries = "1";
        vm.ImportCommand.Execute(null);                                       // the removed one stays removed; Zzccc comes now
        Assert.Equal(["Zzaaa", "Zzccc"], vm.Rows.Select(r => r.Name));
    });

    [Fact]
    public void Recurring_EveryNDays_IsChosenSavedAndShown() => WithApp(app =>
    {
        var vm = app.Get<RecurringViewModel>();
        vm.NewCommand.Execute(null);
        vm.EditName = "Spacer z psem";
        vm.EditAmount = "5";
        vm.EditFrequency = vm.Frequencies.First(f => f.Frequency == RecurrenceFrequency.EveryDays);
        Assert.True(vm.IsEveryDays);
        Assert.False(vm.IsDayOfMonth);
        Assert.False(vm.IsWeekly);

        vm.EditInterval = "0";
        vm.SaveCommand.Execute(null);
        Assert.Equal(Res.Get("RecurBadInterval"), vm.Message);

        vm.EditInterval = "10";
        vm.EditStart = new DateTime(2026, 10, 25);
        vm.SaveCommand.Execute(null);
        var saved = app.Core.Reader.GetRecurringTransactions().Single(t => t.Name == "Spacer z psem");
        Assert.Equal((RecurrenceFrequency.EveryDays, 10), (saved.Frequency, saved.Day));
        Assert.Contains("co 10 dni", vm.Items.First(i => i.Id == saved.Id).Schedule);

        vm.Selected = null;
        vm.Selected = vm.Items.First(i => i.Id == saved.Id);
        Assert.Equal("10", vm.EditInterval);
    });

    // ----- recurring entries: asked as soon as the window is there -----

    [Fact]
    public void Recurring_IsAskedWhenTheWindowIsActivated_NotOnlyAtTheStartOrEveryFewHours() => WithAppAsync(async app =>
    {
        var prompt = app.Get<FakeRecurringPrompt>();
        prompt.CanAsk = false;                                        // the main window is not on screen yet (start of the program)

        var today = DateOnly.FromDateTime(DateTime.Today);
        app.Core.Recurring.Add(new RecurringTransaction
        {
            Name = "Jajka", Type = TransactionType.Expense, Amount = 14m, Frequency = RecurrenceFrequency.Weekly, Day = RecurrenceEngineDay(today),
            StartDate = today.AddDays(-1), ConfirmEach = true,
        });

        var main = app.Get<MainViewModel>();
        await main.CheckRecurringOnActivationAsync();
        Assert.Empty(prompt.Asked);                                   // nobody to ask: the question waits

        prompt.CanAsk = true;                                         // the window comes to the front
        main.ForgetLastRecurringCheck();
        await main.CheckRecurringOnActivationAsync();
        Assert.Single(prompt.Asked);
        Assert.Equal("Jajka", prompt.Asked[0].Title);
    });

    private static int RecurrenceEngineDay(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
}
