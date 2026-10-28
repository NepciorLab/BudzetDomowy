using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BudzetDomowy.App;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.Infrastructure.Updates;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>Appearance, rebindable keys, confirmed recurring entries, running averages, updates, and the smaller table fixes.</summary>
public class V3UiTests
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

    // ----- transactions -----

    [Fact]
    public void Transactions_AllMode_ShowsEverythingWithoutArrows() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("All");
        Assert.True(vm.IsAllMode);
        Assert.False(vm.HasArrows);
        Assert.Equal(app.Core.Reader.GetTransactions().Count, vm.Rows.Count);
        Assert.Equal("Wszystkie wpisy", vm.PeriodTitle);

        vm.PreviousPeriod();                                   // no period to step: nothing changes
        Assert.Equal(app.Core.Reader.GetTransactions().Count, vm.Rows.Count);
        vm.SetModeCommand.Execute("Month");
        Assert.True(vm.HasArrows);
    });

    [Fact]
    public void Transactions_EscClearsTheFilters_OnlyWhenSomeAreSet() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("All");
        Assert.False(vm.ClearFiltersIfAny());

        vm.Search = "lewiatan";
        Assert.True(vm.HasActiveFilters);
        Assert.True(vm.Rows.Count < app.Core.Reader.GetTransactions().Count);
        Assert.True(vm.ClearFiltersIfAny());
        Assert.Equal("", vm.Search);
        Assert.Equal(app.Core.Reader.GetTransactions().Count, vm.Rows.Count);
        Assert.False(vm.ClearFiltersIfAny());
    });

    [Fact]
    public void Transactions_TheSelectedSummaryIsBesideTheOthers_NotBelow() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("All");
        var view = new TransactionsView { DataContext = vm };
        vm.SetSelection(vm.Rows.Take(2).ToList());
        UiHost.Layout(view, 1500, 640);

        var all = UiHost.Descendants<TextBlock>(view).First(t => t.Text == vm.AllLabel);
        var selected = UiHost.Descendants<TextBlock>(view).First(t => t.Text == vm.SelectionLabel);
        var a = all.TranslatePoint(new Point(0, 0), view);
        var b = selected.TranslatePoint(new Point(0, 0), view);
        Assert.True(b.X > a.X + all.ActualWidth, "selected sums are to the right");
        Assert.InRange(Math.Abs(b.Y - a.Y), 0, 12);            // on the same line
    });

    [Fact]
    public void AmountBoxes_RecogniseAnAmountToSelectWhole()
    {
        foreach (var yes in new[] { "12,50", "1 234,56", "1234.5", "-5,00", "12,50 EUR", "  99 " }) Assert.True(AmountBoxes.LooksLikeAmount(yes), yes);
        foreach (var no in new[] { "", "   ", "Lewiatan", "kawa 5", "12,50 euro", "a1,5" }) Assert.False(AmountBoxes.LooksLikeAmount(no), no);
    }

    // ----- appearance -----

    private static void WithPreferences(TestApp app, Action<UiPreferences> test)
    {
        var prefs = UiPreferences.Instance;
        prefs.Initialize(app.Get<SettingsStore>());
        try { test(prefs); }
        finally
        {
            prefs.Scale = UiScaleChoice.Medium;
            prefs.Marker = MarkerShape.Circle;
            prefs.ShowMinus = true;
            prefs.ColorAmounts = true;
        }
    }

    [Fact]
    public void Appearance_TheMinusSignAndColoursFollowTheSettings() => WithApp(app => WithPreferences(app, prefs =>
    {
        var tx = app.Get<TransactionsViewModel>();
        tx.SetModeCommand.Execute("All");
        var expense = tx.Rows.First(r => !r.IsIncome);
        var income = tx.Rows.First(r => r.IsIncome);
        Assert.StartsWith("−", expense.AmountDisplay);
        Assert.StartsWith("+", income.AmountDisplay);

        prefs.ShowMinus = false;
        Assert.DoesNotContain("−", expense.AmountDisplay);
        Assert.DoesNotContain("+", income.AmountDisplay);          // both signs go together
        Assert.Equal("", prefs.SignOf(false));
        Assert.Equal("", prefs.SignOf(true));

        // screens that already built their text rebuild it
        var dashboard = app.Get<DashboardViewModel>();
        Assert.All(dashboard.Recent, r => Assert.DoesNotContain("−", r.AmountText));
        Assert.All(dashboard.Recent, r => Assert.DoesNotContain("+", r.AmountText));

        prefs.ColorAmounts = false;
        Assert.False(app.Get<SettingsStore>().Current.ShowMinus);
        Assert.False(app.Get<SettingsStore>().Current.ColorAmounts);
    }));

    private sealed record SampleAmount(bool IsIncome);

    private static System.Windows.Media.Color ForegroundOf(bool isIncome)
    {
        var box = new TextBlock { Text = "1,00", Style = (Style)Application.Current.FindResource("AmountText"), DataContext = new SampleAmount(isIncome) };
        UiHost.Layout(box, 100, 30);
        return ((System.Windows.Media.SolidColorBrush)box.Foreground).Color;
    }

    [Fact]
    public void Appearance_AmountsKeepTheThemeTextColourWhenColouringIsOff() => WithApp(app => WithPreferences(app, prefs =>
    {
        var red = ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("ExpenseBrush")).Color;
        var green = ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("IncomeBrush")).Color;
        var text = ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("SystemControlForegroundBaseHighBrush")).Color;

        Assert.Equal(green, ForegroundOf(true));
        Assert.Equal(red, ForegroundOf(false));

        prefs.ColorAmounts = false;
        Assert.Equal(text, ForegroundOf(true));                    // the theme's own text colour (white on the dark theme), not black
        Assert.Equal(text, ForegroundOf(false));
        Assert.NotEqual(System.Windows.Media.Colors.Black, text);   // the test theme is the dark one
    }));

    [Fact]
    public void Start_TheChosenPageOpensAtStartup_OrTheLastOne() => WithApp(app =>
    {
        var store = app.Get<SettingsStore>();
        Assert.Equal("dashboard", store.Current.StartPage);                    // default
        Assert.Equal("dashboard", MainViewModel.StartPageKey(store.Current));

        var settings = app.Get<SettingsViewModel>();
        Assert.Equal(9, settings.StartPages.Count);
        Assert.Equal("Ostatnio wybrana", settings.StartPages[0].Label);
        Assert.Equal("Pulpit", settings.SelectedStartPage.Label);

        settings.SelectedStartPage = settings.StartPages.First(p => p.Key == "reports");
        Assert.Equal("reports", store.Current.StartPage);
        Assert.Equal("reports", MainViewModel.StartPageKey(store.Current));

        settings.SelectedStartPage = settings.StartPages[0];                   // "last"
        var main = app.Get<MainViewModel>();
        main.Navigate("budgets");
        Assert.Equal("budgets", store.Current.LastPage);
        Assert.Equal("budgets", MainViewModel.StartPageKey(store.Current));
    });

    [Fact]
    public void Start_ANewMainViewModelOpensOnThePageFromTheSettings() => WithApp(app =>
    {
        var store = app.Get<SettingsStore>();
        store.Update(s => s with { StartPage = "last", LastPage = "events" });
        var main = new MainViewModel(app.Core, app.Get<IEntryLauncher>(), app.Get<TransactionsViewModel>(), app.Get<SettingsViewModel>(),
            app.Get<EventsViewModel>(), app.Get<DashboardViewModel>(), app.Get<BudgetsViewModel>(), app.Get<ReportsViewModel>(),
            app.Get<RecurringViewModel>(), app.Get<PaymentsViewModel>(), app.Get<ToastService>(), app.Get<RecurringRunner>(), store);
        Assert.Equal("events", main.SelectedNav!.Key);

        store.Update(s => s with { StartPage = "settings" });
        var other = new MainViewModel(app.Core, app.Get<IEntryLauncher>(), app.Get<TransactionsViewModel>(), app.Get<SettingsViewModel>(),
            app.Get<EventsViewModel>(), app.Get<DashboardViewModel>(), app.Get<BudgetsViewModel>(), app.Get<ReportsViewModel>(),
            app.Get<RecurringViewModel>(), app.Get<PaymentsViewModel>(), app.Get<ToastService>(), app.Get<RecurringRunner>(), store);
        Assert.Equal("settings", other.SelectedNav!.Key);
    });

    [Fact]
    public void Appearance_ScaleAndSymbolAreChosenInSettings() => WithApp(app => WithPreferences(app, prefs =>
    {
        var vm = app.Get<SettingsViewModel>();
        Assert.Equal(3, vm.Scales.Count);
        vm.SelectedScale = vm.Scales.First(s => s.Choice == UiScaleChoice.Large);
        Assert.Equal(1.2, prefs.ScaleFactor);
        vm.SelectedScale = vm.Scales.First(s => s.Choice == UiScaleChoice.Small);
        Assert.Equal(0.9, prefs.ScaleFactor);
        Assert.Equal(UiScaleChoice.Small, app.Get<SettingsStore>().Current.UiScale);

        vm.SelectedMarker = vm.Markers.First(m => m.Choice == MarkerShape.Bar);
        Assert.Equal(MarkerShape.Bar, app.Get<SettingsStore>().Current.CategoryMarker);
        Assert.Equal(3, vm.Markers.Count);
    }));

    [Fact]
    public void CategoryMarker_DrawsACircleASquareOrAVerticalBar() => WithApp(app => WithPreferences(app, prefs =>
    {
        var circle = new CategoryMarker { Color = "#1E88E5", Size = 12, Shape = MarkerShape.Circle };
        Assert.Equal(12, circle.Width);
        Assert.Equal(12, circle.Height);
        Assert.Equal(6, circle.CornerRadius.TopLeft);

        var square = new CategoryMarker { Color = "#1E88E5", Size = 12, Shape = MarkerShape.Square };
        Assert.Equal(12, square.Width);
        Assert.True(square.CornerRadius.TopLeft < 6);

        var bar = new CategoryMarker { Color = "#1E88E5", Size = 12, Shape = MarkerShape.Bar };
        Assert.True(bar.Width < bar.Height);

        // without an explicit shape it follows the settings
        var follows = new CategoryMarker { Color = "#1E88E5", Size = 12 };
        prefs.Marker = MarkerShape.Bar;
        follows.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.True(follows.Width < follows.Height);

        Assert.NotNull(bar.Background);
        Assert.Equal(System.Windows.Media.Brushes.Transparent, new CategoryMarker { Color = "" }.Background);
    }));

    [Fact]
    public void Categories_PaletteMarksTheColourOfTheSelectedCategory_AndThoseTakenByOthers() => WithApp(app =>
    {
        var vm = app.Get<CategoriesViewModel>();
        var free = vm.Palette.First(p => !p.IsUsed);
        Assert.DoesNotContain(vm.Palette, p => p.IsCurrent);                  // nothing selected: nothing ringed

        vm.SelectedNode = vm.Roots.First();
        vm.EditColor = free.Hex;
        Assert.Equal([free.Hex], vm.Palette.Where(p => p.IsCurrent).Select(p => p.Hex));      // follows the box at once
        vm.SaveCommand.Execute(null);

        Assert.Equal([free.Hex], vm.Palette.Where(p => p.IsCurrent).Select(p => p.Hex));
        Assert.False(vm.Palette.First(p => p.Hex == free.Hex).IsUsed);        // it is this category's own colour, not "taken"

        vm.SelectedNode = vm.Roots.Skip(1).First();                           // another category: the first one's colour is now taken
        Assert.True(vm.Palette.First(p => p.Hex == free.Hex).IsUsed);
        Assert.NotEqual(free.Hex, vm.Palette.FirstOrDefault(p => p.IsCurrent)?.Hex);

        vm.EditColor = free.Hex.ToLowerInvariant();                           // typed in lower case
        Assert.True(vm.Palette.First(p => p.Hex == free.Hex).IsCurrent);
        Assert.DoesNotContain(vm.Palette, p => p.IsCurrent && p.IsUsed);     // the tick and the dot never lie on each other, even if another category has the same colour
        Assert.True(vm.Palette.First(p => p.Hex == free.Hex).IsCurrent);
    });

    [Fact]
    public void SettingsView_HasAnAppearanceTabWithScaleSymbolAndAmounts() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        vm.SelectedTab = SettingsViewModel.AppearanceTab;
        var view = new SettingsView { DataContext = vm };
        UiHost.Layout(view, 1100, 800);
        var texts = UiHost.Texts(view);
        UiHost.Render(view, 1100, 800, "settings-appearance");
        Assert.Contains("Skala interfejsu", texts);
        Assert.Contains("Symbol koloru kategorii", texts);
        Assert.Contains(texts, t => t.Contains("Koloruj kwoty"));
        Assert.Contains("Motyw", texts);
    });

    // ----- shortcuts -----

    [Fact]
    public void ShortcutMap_ParsesAndFormatsKeys()
    {
        Assert.True(ShortcutMap.TryParse("Ctrl+Alt+B", out var key, out var mods));
        Assert.Equal(Key.B, key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, mods);
        Assert.True(ShortcutMap.TryParse("Ctrl+1", out key, out mods));
        Assert.Equal(Key.D1, key);
        Assert.Equal("Ctrl+1", ShortcutMap.Format(Key.D1, ModifierKeys.Control));
        Assert.Equal("Ctrl+Alt+Shift+Win+F5", ShortcutMap.Format(Key.F5, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows));
        Assert.Equal("Alt + ←", ShortcutMap.Display("Alt+Left"));
        Assert.False(ShortcutMap.TryParse("Ctrl+", out _, out _));
        Assert.False(ShortcutMap.TryParse("Ctrl+Nonsense", out _, out _));
        Assert.False(ShortcutMap.TryParse("Hyper+K", out _, out _));
        Assert.False(ShortcutMap.TryParse("Ctrl+Shift", out _, out _));    // a modifier alone is no shortcut
    }

    private static void WithShortcuts(TestApp app, Action<ShortcutMap> test)
    {
        var map = ShortcutMap.Instance;
        map.Initialize(app.Get<SettingsStore>());
        try { test(map); }
        finally { foreach (var d in ShortcutMap.Definitions) map.Reset(d.Id); }
    }

    [Fact]
    public void ShortcutMap_ChangesAKey_RefusesConflictsAndReservedKeys_AndResets() => WithApp(app => WithShortcuts(app, map =>
    {
        Assert.Equal("Ctrl+K", map.KeysOf(ShortcutMap.TxChangeCategory));
        Assert.False(map.IsCustom(ShortcutMap.TxChangeCategory));

        Assert.Null(map.Set(ShortcutMap.TxChangeCategory, Key.J, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("Ctrl+Shift+J", map.KeysOf(ShortcutMap.TxChangeCategory));
        Assert.True(map.IsCustom(ShortcutMap.TxChangeCategory));
        Assert.Equal("Ctrl+Shift+J", app.Get<SettingsStore>().Current.Shortcuts[ShortcutMap.TxChangeCategory]);
        Assert.Equal((Key.J, ModifierKeys.Control | ModifierKeys.Shift), map.GestureOf(ShortcutMap.TxChangeCategory));

        // the old keys are free again
        Assert.Null(map.Set(ShortcutMap.Undo, Key.K, ModifierKeys.Control));

        var taken = map.Set(ShortcutMap.Help, Key.W, ModifierKeys.None);             // W belongs to "new expense"
        Assert.NotNull(taken);
        Assert.Equal("F1", map.KeysOf(ShortcutMap.Help));

        Assert.NotNull(map.Set(ShortcutMap.Help, Key.A, ModifierKeys.None));          // plain letters would break typing
        Assert.NotNull(map.Set(ShortcutMap.Help, Key.C, ModifierKeys.Control));       // the clipboard is the system's
        Assert.NotNull(map.Set(ShortcutMap.Help, Key.Enter, ModifierKeys.Control));
        Assert.Null(map.Set(ShortcutMap.Help, Key.F12, ModifierKeys.None));           // function keys need no modifier

        map.Reset(ShortcutMap.TxChangeCategory);
        Assert.Equal("Ctrl+K", map.KeysOf(ShortcutMap.TxChangeCategory));
        Assert.False(app.Get<SettingsStore>().Current.Shortcuts.ContainsKey(ShortcutMap.TxChangeCategory));
    }));

    [Fact]
    public void Settings_ShortcutRowsEditAndResetKeys() => WithApp(app => WithShortcuts(app, _ =>
    {
        var vm = app.Get<SettingsViewModel>();
        var row = vm.Shortcuts.First(r => r.Id == ShortcutMap.Undo);
        Assert.Equal("Ctrl + Z", row.Keys);
        Assert.False(row.CanReset);
        Assert.Contains(vm.Shortcuts, r => r.IsFixed);                                 // Enter, Tab, … cannot be changed

        vm.EditShortcutCommand.Execute(row);
        Assert.True(row.IsEditing);

        vm.ApplyShortcut(row, Key.LeftCtrl, ModifierKeys.Control);                      // a modifier alone: still waiting
        Assert.True(row.IsEditing);

        vm.ApplyShortcut(row, Key.F1, ModifierKeys.None);                               // already used: stays in edit mode with a reason
        Assert.True(row.IsEditing);
        Assert.NotEqual("", row.Message);

        vm.ApplyShortcut(row, Key.Q, ModifierKeys.Control);
        Assert.False(row.IsEditing);
        Assert.Equal("Ctrl + Q", row.Keys);
        Assert.True(row.CanReset);

        vm.ResetShortcutCommand.Execute(row);
        Assert.Equal("Ctrl + Z", row.Keys);
        Assert.False(row.CanReset);

        vm.EditShortcutCommand.Execute(vm.Shortcuts.First(r => r.IsFixed));            // fixed rows ignore the edit command
        Assert.All(vm.Shortcuts.Where(r => r.IsFixed), r => Assert.False(r.IsEditing));
    }));

    [Fact]
    public void MainWindow_KeyBindingsFollowTheChosenKeys() => WithApp(app => WithShortcuts(app, map =>
    {
        var window = new MainWindow(app.Get<MainViewModel>(), app.Get<SettingsStore>());
        bool Has(Key key, ModifierKeys mods) => window.InputBindings.OfType<KeyBinding>().Any(b => b.Key == key && b.Modifiers == mods);

        Assert.True(Has(Key.F2, ModifierKeys.None));                                    // the list of shortcuts; F1 (the help) is handled for the whole program
        Assert.True(Has(Key.D2, ModifierKeys.Control));
        Assert.Equal(12, window.InputBindings.Count);

        map.Set(ShortcutMap.GoPrefix + "reports", Key.R, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.True(Has(Key.R, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.False(Has(Key.D7, ModifierKeys.Control));
        Assert.Equal(12, window.InputBindings.Count);

        var nav = app.Get<MainViewModel>().NavItems.First(n => n.Key == "reports");
        Assert.Equal("Ctrl + Alt + R", nav.Shortcut);                                   // the navigation hint follows too
    }));

    // ----- recurring entries asking for confirmation -----

    private static async Task<RecurringTransaction> AddConfirmedRentAsync(TestApp app, DateOnly start)
    {
        var template = app.Core.Recurring.Add(new RecurringTransaction
        {
            Type = TransactionType.Expense, Amount = 800m, Frequency = RecurrenceFrequency.Monthly, Day = 5, StartDate = start,
            Place = "Spółdzielnia", Note = "czynsz", ConfirmEach = true,
        });
        await Task.CompletedTask;
        return template;
    }

    [Fact]
    public void RecurringRunner_AsksAndBooksOnlyWhatTheUserConfirms() => WithAppAsync(async app =>
    {
        var prompt = app.Get<FakeRecurringPrompt>();
        var runner = app.Get<RecurringRunner>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var template = await AddConfirmedRentAsync(app, firstOfThisMonth.AddMonths(-2));
        var before = app.Core.Reader.GetTransactions().Count;

        // three terms are due (the 5th of this and the two previous months, as far as already passed)
        var due = Core.Services.RecurrenceEngine.Occurrences(template, template.StartDate, today).ToList();
        prompt.Answers.Enqueue(RecurringAnswer.Yes);
        prompt.Answers.Enqueue(RecurringAnswer.No);
        foreach (var _ in due.Skip(2)) prompt.Answers.Enqueue(RecurringAnswer.Later);

        var outcome = await runner.RunAsync(today);
        Assert.Equal(1, outcome.Created);
        Assert.Equal(1, outcome.Declined);
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);
        var question = prompt.Asked[0];
        Assert.Equal("Spółdzielnia – czynsz", question.Title);
        Assert.Contains("800", question.Amount);

        // a "no" is final, a "yes" is booked: only the postponed terms (if any) come back
        prompt.Asked.Clear();
        var again = await runner.RunAsync(today);
        Assert.Equal(0, again.Created);
        Assert.DoesNotContain(prompt.Asked, q => q.Date == question.Date);
        Assert.Equal(due.Count - 2, again.Postponed);
    });

    [Fact]
    public void RecurringRunner_DoesNotAskWhileThereIsNoWindow_AndAsksAgainOnRequest() => WithAppAsync(async app =>
    {
        var prompt = app.Get<FakeRecurringPrompt>();
        var runner = app.Get<RecurringRunner>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await AddConfirmedRentAsync(app, today.AddDays(-40));

        prompt.CanAsk = false;
        var silent = await runner.RunAsync(today);
        Assert.Empty(prompt.Asked);
        Assert.True(silent.Postponed > 0);
        Assert.Equal(0, silent.Created);

        prompt.CanAsk = true;
        prompt.Answers.Enqueue(RecurringAnswer.Later);        // "later" ends this run's questions
        var first = await runner.RunAsync(today);
        Assert.Single(prompt.Asked);
        Assert.Equal(0, first.Created);

        prompt.Asked.Clear();
        await runner.RunAsync(today);                          // the postponed one is not asked again by the timer…
        Assert.Empty(prompt.Asked);

        prompt.Answers.Enqueue(RecurringAnswer.Yes);
        prompt.Answers.Enqueue(RecurringAnswer.Yes);
        var explicitRun = await runner.RunAsync(today, askAgain: true);   // …but is, when the user presses "generate now"
        Assert.NotEmpty(prompt.Asked);
        Assert.True(explicitRun.Created >= 1);
    });

    [Fact]
    public void RecurringView_HasTheConfirmCheckboxAndSavesIt() => WithApp(app =>
    {
        var vm = app.Get<RecurringViewModel>();
        vm.Selected = vm.Items[0];
        Assert.False(vm.EditConfirm);
        vm.EditConfirm = true;
        vm.SaveCommand.Execute(null);
        Assert.True(app.Core.Reader.GetRecurringTransactions().Single().ConfirmEach);

        var view = new RecurringView { DataContext = vm };
        UiHost.Layout(view, 1050, 700);
        Assert.Contains("Potwierdzaj wykonanie", UiHost.Descendants<CheckBox>(view).Select(c => c.Content?.ToString()));

        vm.NewCommand.Execute(null);
        Assert.False(vm.EditConfirm);
    });

    // ----- running average chart -----

    [Fact]
    public void Reports_AveragesChartShowsTheRunningAveragePerYear() => WithApp(app =>
    {
        var compare = app.Get<ReportsViewModel>().Compare;
        Assert.True(compare.IsYears);

        compare.SetChartStyleCommand.Execute("Averages");
        Assert.True(compare.IsAverages);
        Assert.NotEmpty(compare.Series);
        Assert.All(compare.Series, x => Assert.IsType<LiveChartsCore.SkiaSharpView.LineSeries<double?>>(x));
        Assert.Contains("średnia miesięczna narastająco", compare.Title);

        // running averages are a per-year view: months side by side fall back to columns
        compare.SetKindCommand.Execute("Months");
        Assert.True(compare.IsColumns);
        compare.SetChartStyleCommand.Execute("Averages");
        Assert.True(compare.IsColumns);
    });

    // ----- updates from GitHub -----

    private const string ReleaseUrl = "https://api.github.com/repos/NepciorLab/BudzetDomowy/releases/latest";

    private static string ReleaseJson(string tag, string assetName = "BudzetDomowy.exe", long size = 0) => $$"""
        {"tag_name":"{{tag}}","html_url":"https://github.com/NepciorLab/BudzetDomowy/releases/tag/{{tag}}","body":"Nowości",
         "assets":[{"name":"{{assetName}}","url":"https://api.github.com/assets/1","browser_download_url":"https://github.com/dl/{{assetName}}","size":{{size}}}]}
        """;

    [Fact]
    public void GitHubReleases_ReadsTheTagTheNotesAndPicksTheProgramFile()
    {
        var release = GitHubReleases.Parse("""
            {"tag_name":"v0.3.1","html_url":"u","body":"b","assets":[
              {"name":"BudzetDomowy-0.3.1.zip","url":"a1","browser_download_url":"d1","size":10},
              {"name":"BudzetDomowy.exe","url":"a2","browser_download_url":"d2","size":20},
              {"name":"notes.txt","url":"a3","browser_download_url":"d3","size":1}]}
            """);
        Assert.Equal(new Version(0, 3, 1), release!.Version);
        Assert.Equal("BudzetDomowy.exe", release.Asset!.Name);              // the exe wins over the archive
        Assert.Equal(20, release.Asset.Size);

        var zipOnly = GitHubReleases.Parse("""{"tag_name":"0.4.0","assets":[{"name":"BudzetDomowy-0.4.0.zip","url":"a","browser_download_url":"d","size":5}]}""");
        Assert.Equal("BudzetDomowy-0.4.0.zip", zipOnly!.Asset!.Name);
        Assert.Null(GitHubReleases.Parse("""{"tag_name":"latest","assets":[]}"""));
    }

    [Fact]
    public void Updates_CheckReportsNewerSameMissingAndBrokenReleases() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var updates = app.Get<UpdateService>();                              // pretends to be version 0.1.0

        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0")));
        var newer = await updates.CheckAsync();
        Assert.Equal(UpdateState.Available, newer.State);
        Assert.Contains("0.2.0", newer.Message);

        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.1.0")));
        Assert.Equal(UpdateState.UpToDate, (await updates.CheckAsync()).State);

        github.Responses[ReleaseUrl] = (HttpStatusCode.NotFound, []);        // a private repository without a token looks like this
        var missing = await updates.CheckAsync();
        Assert.Equal(UpdateState.NotFound, missing.State);
        Assert.Contains("wydania", missing.Message);

        github.Responses[ReleaseUrl] = (HttpStatusCode.InternalServerError, []);
        Assert.Equal(UpdateState.Failed, (await updates.CheckAsync()).State);

        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes("not json"));
        Assert.Equal(UpdateState.Failed, (await updates.CheckAsync()).State);
    });

    [Fact]
    public void Updates_AskGitHubAnonymously() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.1.0")));

        await app.Get<UpdateService>().CheckAsync();
        Assert.Null(github.Requests[^1].Headers.Authorization);               // the repository is public: no credentials are needed or sent
        Assert.Contains("BudzetDomowy", github.Requests[^1].Headers.UserAgent.ToString());
    });

    private static byte[] ProgramBytes() => File.ReadAllBytes(typeof(UpdateService).Assembly.Location);   // a real PE file with a real version

    private static byte[] ZipWithExe(byte[] exe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("BudzetDomowy.exe");
            using (var s = entry.Open()) s.Write(exe);
            zip.CreateEntry("Source/readme.txt");
        }
        return ms.ToArray();
    }

    [Fact]
    public void Updates_InstallReplacesTheExe_KeepsTheOldOne_AndRestarts() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var updates = app.Get<UpdateService>();
        var restarter = (FakeRestarter)app.Get<IAppRestarter>();
        var exePath = Path.Combine(Path.GetTempPath(), "x");                  // replaced below by the service's own path
        exePath = (string)typeof(UpdateService).GetField("_exePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(updates)!;
        File.WriteAllBytes(exePath, "MZ old program"u8.ToArray());

        var zip = ZipWithExe(ProgramBytes());
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0", "BudzetDomowy-0.2.0.zip", zip.Length)));
        github.Responses["https://github.com/dl/BudzetDomowy-0.2.0.zip"] = (HttpStatusCode.OK, zip);

        var check = await updates.CheckAsync();
        var progress = new List<double>();
        var error = await updates.InstallAsync(check.Release!, new Progress<double>(progress.Add));

        Assert.Null(error);
        Assert.Equal(ProgramBytes(), File.ReadAllBytes(exePath));
        Assert.Equal("MZ old program"u8.ToArray(), File.ReadAllBytes(exePath + ".old"));
        Assert.True(updates.CanInstall);

        UpdateInstaller.CleanUp(exePath);
        Assert.False(File.Exists(exePath + ".old"));

        updates.Restart();
        Assert.Equal(1, restarter.Restarts);
    });

    [Fact]
    public void Updates_AnIncompleteOrWrongDownloadChangesNothing() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var updates = app.Get<UpdateService>();
        var exePath = (string)typeof(UpdateService).GetField("_exePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(updates)!;
        File.WriteAllBytes(exePath, "MZ old program"u8.ToArray());
        var original = File.ReadAllBytes(exePath);

        // shorter than announced
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0", "BudzetDomowy.exe", 5000)));
        github.Responses["https://github.com/dl/BudzetDomowy.exe"] = (HttpStatusCode.OK, ProgramBytes()[..100]);
        var release = (await updates.CheckAsync()).Release!;
        Assert.NotNull(await updates.InstallAsync(release));
        Assert.Equal(original, File.ReadAllBytes(exePath));

        // not a program at all
        var junk = Encoding.UTF8.GetBytes("this is not an exe");
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0", "BudzetDomowy.exe", junk.Length)));
        github.Responses["https://github.com/dl/BudzetDomowy.exe"] = (HttpStatusCode.OK, junk);
        Assert.NotNull(await updates.InstallAsync((await updates.CheckAsync()).Release!));
        Assert.Equal(original, File.ReadAllBytes(exePath));

        // a program that is not newer than the running one (the service pretends to be 0.1.0; here it claims to be the version of this very file)
        var same = new UpdateService(app.Get<SettingsStore>(), app.Get<GitHubReleases>(), app.Get<FakeUpdatePrompt>(), app.Get<IAppRestarter>(), exePath, typeof(UpdateService).Assembly.GetName().Version!);
        var bytes = ProgramBytes();
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v9.0.0", "BudzetDomowy.exe", bytes.Length)));
        github.Responses["https://github.com/dl/BudzetDomowy.exe"] = (HttpStatusCode.OK, bytes);
        Assert.NotNull(await same.InstallAsync((await same.CheckAsync()).Release!));
        Assert.Equal(original, File.ReadAllBytes(exePath));
        Assert.False(File.Exists(exePath + ".old"));

        // no program file in the release
        var empty = GitHubReleases.Parse("""{"tag_name":"v0.9.0","assets":[]}""")!;
        Assert.NotNull(await updates.InstallAsync(empty));
    });

    [Fact]
    public void UpdateInstaller_PutsTheOldFileBackWhenTheReplacementFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "budzet-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "BudzetDomowy.exe");
            File.WriteAllText(target, "old");
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Replace(Path.Combine(dir, "missing.exe"), target));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".old"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Updates_TheStartupCheckRespectsTheSettingAndTheAnswer() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var updates = app.Get<UpdateService>();
        var prompt = app.Get<FakeUpdatePrompt>();
        var store = app.Get<SettingsStore>();
        var restarter = (FakeRestarter)app.Get<IAppRestarter>();
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0")));

        store.Update(s => s with { AutoUpdate = false });
        await updates.RunAutomaticAsync();
        Assert.Empty(github.Requests);                                       // switched off: not even a request

        store.Update(s => s with { AutoUpdate = true });
        prompt.Answer = false;
        await updates.RunAutomaticAsync();
        Assert.Equal(1, prompt.Asked);                                       // asked, declined: nothing installed
        Assert.Equal(0, restarter.Restarts);

        // nothing newer: no question
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.1.0")));
        await updates.RunAutomaticAsync();
        Assert.Equal(1, prompt.Asked);

        // no connection to a private repository: silent
        github.Responses[ReleaseUrl] = (HttpStatusCode.NotFound, []);
        await updates.RunAutomaticAsync();
        Assert.Equal(1, prompt.Asked);
    });

    [Fact]
    public void Settings_CheckForUpdatesShowsTheResultAndTheInstallButton() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var vm = app.Get<SettingsViewModel>();
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.5.0")));

        await vm.CheckUpdateCommand.ExecuteAsync(null);
        Assert.True(vm.UpdateAvailable);
        Assert.Contains("0.5.0", vm.UpdateMessage);
        Assert.False(vm.UpdateBusy);

        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.1.0")));
        await vm.CheckUpdateCommand.ExecuteAsync(null);
        Assert.False(vm.UpdateAvailable);
        Assert.Equal("Masz najnowszą wersję.", vm.UpdateMessage);

        vm.AutoUpdate = false;
        Assert.False(app.Get<SettingsStore>().Current.AutoUpdate);
    });
}
