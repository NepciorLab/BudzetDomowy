using System.Windows;
using BudzetDomowy.App.Controls;
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
        foreach (var day in new[] { 10, 12, 20 })
            app.Core.Transactions.Add(new DateOnly(2020, 3, day), TransactionType.Expense, 5m, null, "zz", null);
        var vm = app.Get<TransactionsViewModel>();
        vm.Anchor = new DateOnly(2020, 3, 10);                        // a Tuesday

        vm.SetModeCommand.Execute("Day");
        Assert.True(vm.IsDayMode);
        Assert.True(vm.HasArrows);
        Assert.Equal("Wtorek, 10 marca 2020", vm.PeriodTitle);
        Assert.All(vm.Rows, r => Assert.Equal(new DateOnly(2020, 3, 10), r.Model.Date));

        vm.NextPeriod();                                              // the 11th has no entries: the arrow leaps to the 12th
        Assert.Equal(new DateOnly(2020, 3, 12), vm.Anchor);
        vm.PreviousPeriod();
        Assert.Equal(new DateOnly(2020, 3, 10), vm.Anchor);
        vm.PreviousPeriod();                                          // nothing earlier at all
        Assert.Equal(new DateOnly(2020, 3, 10), vm.Anchor);
        Assert.False(vm.CanGoPrevious);

        vm.Anchor = new DateOnly(2026, 10, 25);
        vm.SetModeCommand.Execute("Week");
        Assert.True(vm.IsWeekMode);
        Assert.False(vm.IsDayMode);
        Assert.Equal("19.10 – 25.10.2026", vm.PeriodTitle);
        Assert.DoesNotContain("ydzie", vm.PeriodTitle);
    });

    [Fact]
    public void Transactions_ACategoryFilter_CanLeaveOutItsSubcategories() => WithApp(app =>
    {
        var categories = app.Core.Reader.GetCategories();
        var parent = categories.First(c => c.ParentId is null && categories.Any(k => k.ParentId == c.Id));
        var child = categories.First(c => c.ParentId == parent.Id);
        var day = new DateOnly(2026, 10, 14);
        app.Core.Transactions.Add(day, TransactionType.Expense, 11m, parent.Id, "zzmain", null);
        app.Core.Transactions.Add(day, TransactionType.Expense, 22m, child.Id, "zzsub", null);

        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("All");
        Assert.True(vm.IncludeSubcategories);
        Assert.False(vm.HasCategoryFilter);

        vm.CategoryFilter = parent.Id;
        Assert.True(vm.HasCategoryFilter);
        Assert.Contains(vm.Rows, r => r.Model.Note == "zzmain");
        Assert.Contains(vm.Rows, r => r.Model.Note == "zzsub");                // the default: with the subcategories

        vm.IncludeSubcategories = false;
        Assert.Contains(vm.Rows, r => r.Model.Note == "zzmain");
        Assert.DoesNotContain(vm.Rows, r => r.Model.Note == "zzsub");
        Assert.All(vm.Rows, r => Assert.Equal(parent.Id, r.Model.CategoryId));

        Assert.True(vm.ClearFiltersIfAny());
        Assert.True(vm.IncludeSubcategories);                                   // cleared filters start from the default again
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

    [Fact]
    public void Charts_NeverHaveAnEmptyAxisList_EvenBeforeAnyDataIsThere() => WithApp(app =>
    {
        // LiveCharts throws "XAxes and YAxes must contain at least one element" when it measures a chart without axes
        var reports = app.Get<ReportsViewModel>();
        Assert.NotEmpty(reports.TrendXAxes);
        Assert.NotEmpty(reports.TrendYAxes);
        Assert.NotEmpty(reports.SumXAxes);
        Assert.NotEmpty(reports.SumYAxes);

        var compare = new CompareViewModel(app.Core);
        Assert.NotEmpty(compare.XAxes);
        Assert.NotEmpty(compare.YAxes);

        using var empty = new TestApp(sampleData: false);
        var charts = new ChartsViewModel(empty.Core) { RebuildDelay = TimeSpan.Zero };
        charts.Rebuild();
        Assert.NotEmpty(charts.XAxes);
        Assert.NotEmpty(charts.YAxes);
    });

    [Fact]
    public void Transactions_ASearchThatIsNull_DoesNotBreakTheTable() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        var before = vm.Rows.Count;
        vm.Search = null!;                                                    // what a broken binding could write
        vm.AmountFrom = null!;
        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal("", vm.Search);
        Assert.Equal(before, vm.Rows.Count);
        Assert.False(vm.HasActiveFilters);
    });

    [Fact]
    public void Dashboard_ArrowsLeadOnlyToMonthsWithEntries() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var thisMonth = vm.Month;
        Assert.False(vm.CanGoNext);                                           // the sample data ends today and the present month is the last one
        vm.NextMonthCommand.Execute(null);
        Assert.Equal(thisMonth, vm.Month);

        Assert.True(vm.CanGoPrevious);
        vm.PreviousMonthCommand.Execute(null);
        vm.PreviousMonthCommand.Execute(null);
        Assert.Equal(thisMonth.AddMonths(-2), vm.Month);                      // the oldest entry of the sample data
        Assert.False(vm.CanGoPrevious);
        vm.PreviousMonthCommand.Execute(null);
        Assert.Equal(thisMonth.AddMonths(-2), vm.Month);
    });

    [Fact]
    public void Dashboard_TheDonutKeepsItsSlices_AndOnlyTheirSizesChangeWithTheMonth() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var series = vm.PieSeries;
        var groceries = series.OfType<LiveChartsCore.SkiaSharpView.PieSeries<LiveChartsCore.Defaults.ObservableValue>>().Single(s => s.Name == "Spożywcze");
        var now = groceries.Values!.Single().Value;

        vm.PreviousMonthCommand.Execute(null);                                // last month had 640 in groceries
        Assert.Same(series, vm.PieSeries);                                    // the same collection: the chart is not given a new one to draw from the centre
        Assert.Contains(groceries, series);                                   // and the very same series, only with another value
        Assert.Equal(640d, groceries.Values!.Single().Value);
        Assert.NotEqual(now, groceries.Values!.Single().Value);

        vm.NextMonthCommand.Execute(null);
        Assert.Equal(now, groceries.Values!.Single().Value);
    });

    [Fact]
    public void Dashboard_TheBars_ShareTheSlicesOfTheDonut_AndAreNotReplacedByAnotherMonth() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var bars = vm.BarItems;
        var groceries = bars.Single(b => b.Name == "Spożywcze");
        Assert.Equal(vm.PieSeries.Count(), bars.Count);                        // one bar for every slice of the ring

        vm.PreviousMonthCommand.Execute(null);
        Assert.Same(bars, vm.BarItems);                                        // not given a new collection: the chart does not start again from the bottom
        Assert.Contains(groceries, bars);
        Assert.Equal(640d, groceries.Amount);                                  // the same figure as the ring
    });

    [Fact]
    public void Dashboard_View_HasTheCategoriesOnTheWholeWidth_AndBelowThemTheRecentEntriesBesideTheBudgets() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var view = new BudzetDomowy.App.Views.DashboardView { DataContext = vm };
        UiHost.Layout(view, 1500, 1100);
        UiHost.Render(view, 1500, 1100, "dashboard-layout");

        System.Windows.Point At(string text) => UiHost.Descendants<System.Windows.Controls.TextBlock>(view).First(t => t.Text == text).TranslatePoint(new System.Windows.Point(), view);
        var recent = At("Ostatnie wpisy");
        var budgets = At("Budżety");
        var categories = At(vm.ChartTitle);
        Assert.True(categories.Y < recent.Y);                                  // the categories first, the recent entries below them
        Assert.True(categories.Y < budgets.Y);
        Assert.True(Math.Abs(budgets.Y - recent.Y) < 5);                       // the budgets on the same level as the recent entries
        Assert.True(budgets.X > recent.X + 300);                               // and beside them
        var bars = Assert.IsType<BarBreakdownChart>(view.FindName("CategoryBars"));
        Assert.True(bars.ActualWidth > 150);
    });

    [Fact]
    public void Dashboard_Bars_StandAtTheLeft_TheGridReachesHalfABarBeyondThem_AndTheTipTellsTheAmountOfTheBar() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var view = new BudzetDomowy.App.Views.DashboardView { DataContext = vm };
        UiHost.Layout(view, 1500, 1100);
        var chart = Assert.IsType<BarBreakdownChart>(view.FindName("CategoryBars"));
        chart.CompleteAnimation();
        UiHost.Layout(view, 1500, 1100);
        UiHost.Render(view, 1500, 1100, "dashboard-bars");

        var column = (System.Windows.Controls.Grid)chart.Parent;
        Assert.Equal(HorizontalAlignment.Left, chart.HorizontalAlignment);
        Assert.True(chart.ActualWidth < column.ColumnDefinitions[2].ActualWidth - 20);          // not spread over the whole column
        Assert.Equal(chart.PlotRight, chart.ActualWidth, 0.5);                                  // nothing beyond the grid: it ends half a bar after the last bar
        Assert.Equal(chart.BarWidth * 2, chart.SlotWidth, 0.001);                               // slot = bar + half a bar on each side

        var first = vm.BarItems[0];
        var inFirstBar = new Point(chart.PlotLeft + chart.SlotWidth / 2, BarBreakdownChart.PlotHeight + 9);
        Assert.Same(first, chart.ItemAt(inFirstBar));                                           // the bottom of the bar, or the name below it, belongs to the bar
        Assert.Null(chart.ItemAt(new Point(chart.PlotLeft - 3, BarBreakdownChart.PlotHeight)));
        Assert.Null(chart.ItemAt(new Point(chart.PlotRight + 3, BarBreakdownChart.PlotHeight)));
        Assert.Same(vm.BarItems[1], chart.ItemAt(new Point(chart.PlotLeft + chart.SlotWidth * 1.5, BarBreakdownChart.PlotHeight + 9)));

        var tip = chart.TipOf(first);                                                           // one bar: its amount, not a list for the amount
        Assert.StartsWith(first.Name + ":", tip);
        Assert.Contains("zł", tip);
        Assert.EndsWith("%)", tip);
        Assert.DoesNotContain("\n", tip);
    });

    [Fact]
    public void Dashboard_BarScale_IsRoundedUpToNiceSteps() => WithApp(app =>
    {
        Assert.Equal(1000, BarBreakdownChart.NiceMax(900));                    // four divisions of 250
        Assert.Equal(1000, BarBreakdownChart.NiceMax(1000));
        Assert.Equal(2000, BarBreakdownChart.NiceMax(1100));                   // divisions of 500
        Assert.Equal(4000, BarBreakdownChart.NiceMax(2300));
        Assert.Equal(1, BarBreakdownChart.NiceMax(0));
    });

    [Fact]
    public void Dashboard_AfterDrillingIn_TheSlicesThatAreGone_LeaveTheRing_SoNobodyListsThemInATooltip() => WithAppAsync(async app =>
    {
        var categories = app.Core.Reader.GetCategories();
        var parent = categories.First(c => c.ParentId is null && categories.Any(k => k.ParentId == c.Id));
        var children = categories.Where(c => c.ParentId == parent.Id).Take(2).ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var child in children) app.Core.Transactions.Add(today, TransactionType.Expense, 33m, child.Id, "zz", null);
        app.Core.Transactions.Add(today, TransactionType.Expense, 20m, parent.Id, "zz", null);

        var vm = app.Get<DashboardViewModel>();
        var slice = vm.Slices.First(s => s.Id == parent.Id);
        Assert.True(slice.CanDrill);
        vm.DrillIntoCommand.Execute(slice);
        Assert.True(vm.PieSeries.Count() > vm.Slices.Count);                   // the main categories are still shrinking away
        await Task.Delay(1500);
        Assert.Equal(vm.Slices.Count, vm.PieSeries.Count());                   // and then they are gone, with their bars
        Assert.Equal(vm.Slices.Count, vm.BarItems.Count);

        vm.BackCommand.Execute(null);
        await Task.Delay(1500);
        Assert.Equal(vm.Slices.Count, vm.PieSeries.Count());
        Assert.Equal(vm.Slices.Count, vm.BarItems.Count);
    });

    [Fact]
    public void Dashboard_SlicesOfTheSameColour_GetShadesOfIt() => WithApp(_ =>
    {
        var colors = ChartFactory.Distinguish(["#1E88E5", "#1E88E5", "#43A047", "#1e88e5"]);
        Assert.Equal("#43A047", colors[2]);                                    // a colour that is alone stays as it is
        Assert.Equal(4, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(new[] { 0, 1, 3 }, i => Assert.Matches("^#[0-9A-F]{6}$", colors[i]));
        Assert.True(Lightness(colors[0]) < Lightness(colors[1]) && Lightness(colors[1]) < Lightness(colors[3]));   // from darker to lighter
        Assert.Equal(["#1E88E5", "#43A047"], ChartFactory.Distinguish(["#1E88E5", "#43A047"]));
    });

    private static int Lightness(string hex) => Convert.ToInt32(hex[1..3], 16) + Convert.ToInt32(hex[3..5], 16) + Convert.ToInt32(hex[5..7], 16);

    [Fact]
    public void Dashboard_TheMonthTitle_OpensAListOfMonthsAndYears() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var thisMonth = vm.Month;
        Assert.False(vm.IsPickerOpen);
        vm.TogglePickerCommand.Execute(null);
        Assert.True(vm.IsPickerOpen);
        Assert.Equal(thisMonth.Year, vm.PickerYear);
        Assert.Equal(12, vm.PickerMonths.Count);
        Assert.Single(vm.PickerMonths, m => m.IsCurrent);
        Assert.Equal(thisMonth.Month, vm.PickerMonths.Single(m => m.IsCurrent).Number);
        Assert.All(vm.PickerMonths.Where(m => m.Number > thisMonth.Month), m => Assert.True(m.IsFuture));
        Assert.True(vm.PickerMonths.Single(m => m.IsCurrent).HasData);

        Assert.False(vm.CanPickerGoForward);
        vm.ShiftPickerYearCommand.Execute("10");                              // there is no later year
        Assert.Equal(thisMonth.Year, vm.PickerYear);
        vm.ShiftPickerYearCommand.Execute("-10");                             // ten years at once
        Assert.Equal(thisMonth.Year - 10, vm.PickerYear);
        Assert.True(vm.CanPickerGoForward);
        Assert.All(vm.PickerMonths, m => { Assert.False(m.IsFuture); Assert.False(m.HasData); });
        vm.ShiftPickerYearCommand.Execute("-1");
        Assert.Equal(thisMonth.Year - 11, vm.PickerYear);
        vm.ShiftPickerYearCommand.Execute("2");
        Assert.Equal(thisMonth.Year - 9, vm.PickerYear);

        vm.PickMonthCommand.Execute(3);
        Assert.False(vm.IsPickerOpen);
        Assert.Equal(new DateOnly(thisMonth.Year - 9, 3, 1), vm.Month);
        Assert.Equal("Marzec " + (thisMonth.Year - 9), vm.MonthTitle);
    });

    [Fact]
    public void Dashboard_AMonthThatHasNotBegun_CannotBePicked() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today.Month == 12) return;                                        // no later month in this year
        var before = vm.Month;
        vm.TogglePickerCommand.Execute(null);
        vm.PickMonthCommand.Execute(today.Month + 1);
        Assert.Equal(before, vm.Month);
    });

    [Fact]
    public void Dashboard_TheRunningMonth_IsComparedWithTheSameDaysOfThePreviousMonth_AndSaysSo() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = new DateOnly(today.Year, today.Month, 1);
        var previous = start.AddMonths(-1);
        var previousEnd = new DateOnly(previous.Year, previous.Month, Math.Min(today.Day, DateTime.DaysInMonth(previous.Year, previous.Month)));
        var all = app.Core.Reader.GetTransactions();
        var now = BudzetDomowy.Core.Services.PeriodReports.Summary(all, start, today).Expense;
        var then = BudzetDomowy.Core.Services.PeriodReports.Summary(all, previous, previousEnd).Expense;
        Assert.True(then > 0);

        Assert.Contains("poprzedniego okresu", vm.ExpenseChange);
        Assert.DoesNotContain("miesiąca", vm.ExpenseChange);
        var percent = Math.Abs((now - then) / then * 100m).ToString("0", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Contains(percent + "%", vm.ExpenseChange);
        Assert.True(vm.HasChangeTip);
        Assert.Contains($"1\u2013{today.Day} ", vm.ChangeTip);
        Assert.Contains($"1\u2013{previousEnd.Day} ", vm.ChangeTip);

        vm.PreviousMonthCommand.Execute(null);                                // a finished month: the whole month against the whole one before
        Assert.False(vm.HasChangeTip);
        if (vm.ExpenseChange.Length > 0) Assert.Contains("miesiąca", vm.ExpenseChange);
        Assert.DoesNotContain("okresu", vm.IncomeChange + vm.ExpenseChange);
    });

    // ----- the colour of the subcategories -----

    [Fact]
    public void Categories_WhenSubcategoriesTakeTheColourOfTheMainOne_TheyFollowItAndAreNotPickedByHand() => WithApp(app =>
    {
        var prefs = UiPreferences.Instance;
        prefs.SubcategoriesInheritColor = true;
        try
        {
            var vm = app.Get<CategoriesViewModel>();
            vm.SelectedNode = vm.Roots.First(r => r.Children.Count > 0);
            var rootId = vm.SelectedNode.Model.Id;
            Assert.True(vm.CanEditColor);                                     // a main category keeps its colour box

            vm.EditColor = "#112233";
            vm.SaveCommand.Execute(null);
            var below = app.Core.Reader.GetCategories().Where(c => c.ParentId == rootId).ToList();
            Assert.NotEmpty(below);
            Assert.All(below, c => Assert.Equal("#112233", c.Color));         // the subcategories followed

            var child = vm.Roots.First(r => r.Model.Id == rootId).Children.First();
            vm.SelectedNode = child;
            Assert.False(vm.CanEditColor);                                    // and cannot be given another colour
            vm.EditColor = "#445566";
            vm.SaveCommand.Execute(null);
            Assert.Equal("#112233", app.Core.Reader.GetCategories().First(c => c.Id == child.Model.Id).Color);

            prefs.SubcategoriesInheritColor = false;
            Assert.True(vm.CanEditColor);
            vm.EditColor = "#445566";
            vm.SaveCommand.Execute(null);
            Assert.Equal("#445566", app.Core.Reader.GetCategories().First(c => c.Id == child.Model.Id).Color);
        }
        finally
        {
            prefs.SubcategoriesInheritColor = false;
        }
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
