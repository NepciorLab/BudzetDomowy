using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>Period browsing, selection summaries, comparison reports, folder changes and the table preferences.</summary>
public class V2UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    // ----- transactions -----

    [Fact]
    public void Transactions_StartOnTheCurrentMonth_AndStepBetweenPeriods() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        vm.Reload();

        Assert.Equal(ViewMode.Month, vm.Mode);
        Assert.All(vm.Rows, r => Assert.Equal(today.Month, r.Model.Date.Month));
        var thisMonth = vm.Rows.Count;

        vm.PreviousPeriod();
        Assert.Equal(today.AddMonths(-1).Month, vm.Anchor.Month);
        Assert.All(vm.Rows, r => Assert.Equal(today.AddMonths(-1).Month, r.Model.Date.Month));
        Assert.True(vm.Rows.Count >= 2);                 // sample data: spendings and a salary a month ago

        vm.TodayCommand.Execute(null);
        Assert.Equal(thisMonth, vm.Rows.Count);

        vm.SetModeCommand.Execute("Week");
        Assert.All(vm.Rows, r => Assert.InRange(r.Model.Date, today.AddDays(-6), today));
        var week = vm.Rows.Count;
        vm.NextPeriod();
        Assert.Empty(vm.Rows);                           // nothing is booked in the future
        vm.PreviousPeriod();
        Assert.Equal(week, vm.Rows.Count);

        vm.SetModeCommand.Execute("Year");
        Assert.All(vm.Rows, r => Assert.Equal(today.Year, r.Model.Date.Year));
        Assert.Equal(today.Year.ToString(), vm.PeriodTitle);
    });

    [Fact]
    public void Transactions_ShowTotalsOfTheMatchingRows_AndSeparatelyOfTheSelectedOnes() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("Range");
        vm.From = DateTime.Today.AddMonths(-4);
        vm.To = DateTime.Today.AddDays(1);
        vm.Reload();

        Assert.False(vm.HasSelection);
        Assert.StartsWith("Wszystkie pasujące: " + vm.Rows.Count, vm.AllLabel);

        var chosen = vm.Rows.Where(r => !r.IsIncome).Take(2).ToList();
        vm.SetSelection(chosen);
        Assert.True(vm.HasSelection);
        Assert.StartsWith("Zaznaczone: 2", vm.SelectionLabel);
        var sum = chosen.Sum(r => r.Model.AmountPln);
        Assert.Contains(sum.ToString("C2", System.Globalization.CultureInfo.CurrentCulture), vm.SelectionExpenseText);
        Assert.StartsWith("Wszystkie pasujące: " + vm.Rows.Count, vm.AllLabel);   // the first group does not shrink

        var cleared = false;
        vm.SelectionClearRequested += () => cleared = true;
        vm.ClearSelection();
        Assert.True(cleared);
        vm.SetSelection([]);
        Assert.False(vm.HasSelection);
    });

    [Fact]
    public void TransactionsView_HidesTheColumnsTheUserTurnedOff() => WithApp(app =>
    {
        var prefs = UiPreferences.Instance;
        prefs.Initialize(app.Get<SettingsStore>());
        prefs.SetHiddenColumns("transactions", [1]);
        try
        {
            var vm = app.Get<TransactionsViewModel>();
            var view = new TransactionsView { DataContext = vm };
            UiHost.Layout(view, 1080, 640);
            var grid = Assert.IsType<DataGrid>(view.FindName("Grid"));
            GridBehaviors.ApplyHidden(grid);              // what the Loaded event does once the view is on screen
            Assert.Equal(Visibility.Collapsed, grid.Columns[1].Visibility);
            Assert.Equal(Visibility.Visible, grid.Columns[0].Visibility);
        }
        finally
        {
            prefs.SetHiddenColumns("transactions", []);
        }
    });

    [Fact]
    public void Preferences_RememberAutoFitAndHiddenColumns() => WithApp(app =>
    {
        var store = app.Get<SettingsStore>();
        var prefs = UiPreferences.Instance;
        prefs.Initialize(store);
        prefs.AutoFitColumns = true;
        prefs.SetHiddenColumns("places", [2, 0, 2]);
        try
        {
            Assert.True(store.Current.AutoFitColumns);
            Assert.Equal([0, 2], prefs.HiddenColumns("places"));
            prefs.SetHiddenColumns("places", []);
            Assert.Empty(prefs.HiddenColumns("places"));
            Assert.False(store.Current.HiddenColumns.ContainsKey("places"));
        }
        finally
        {
            prefs.AutoFitColumns = false;
        }
    });

    // ----- reports -----

    [Fact]
    public void Reports_CompareYearsMonthByMonth_ForACategory() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var compare = vm.Compare;
        var thisYear = DateTime.Today.Year;

        Assert.True(compare.IsYears);
        Assert.Contains(compare.Picks, p => p.IsSelected && (int)p.Key == thisYear);
        Assert.True(compare.HasData);
        Assert.NotEmpty(compare.Totals);

        // untick every year: nothing left to draw
        foreach (var p in compare.Picks) p.IsSelected = false;
        Assert.False(compare.HasData);

        compare.Picks.First(p => (int)p.Key == thisYear).IsSelected = true;
        var groceries = compare.Choices.First(c => c.Name == "Spożywcze");
        compare.Category = groceries;
        Assert.True(compare.HasData);
        var total = compare.Totals.Single();
        Assert.Equal(thisYear.ToString(), total.Name);
        Assert.Contains("średnio", total.Average);

        compare.SetTypeCommand.Execute("Income");
        Assert.True(compare.IsIncome);
        Assert.DoesNotContain(compare.Choices, c => c.Name == "Spożywcze");   // expense categories are not offered for income
    });

    [Fact]
    public void Reports_TabStripAndTabStayInSync() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        vm.TabIndex = 2;
        Assert.Equal(ReportTab.Places, vm.Tab);
        Assert.True(vm.IsPlaces);
        vm.SetTabCommand.Execute("Compare");
        Assert.Equal(1, vm.TabIndex);
        vm.TabIndex = 99;                         // nonsense is ignored
        Assert.Equal(ReportTab.Compare, vm.Tab);
    });

    [Fact]
    public void Reports_ChartsCanBeSwitchedFromColumnsToLines() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        Assert.All(vm.Compare.Series, x => Assert.IsType<LiveChartsCore.SkiaSharpView.ColumnSeries<double>>(x));
        vm.Compare.SetChartStyleCommand.Execute("Lines");
        Assert.True(vm.Compare.IsLines);
        Assert.NotEmpty(vm.Compare.Series);
        Assert.All(vm.Compare.Series, x => Assert.IsType<LiveChartsCore.SkiaSharpView.LineSeries<double>>(x));
        vm.Compare.SetKindCommand.Execute("Months");
        Assert.All(vm.Compare.Series, x => Assert.IsType<LiveChartsCore.SkiaSharpView.LineSeries<double>>(x));

        Assert.All(vm.TrendSeries, x => Assert.IsType<LiveChartsCore.SkiaSharpView.ColumnSeries<double>>(x));
        vm.SetTrendStyleCommand.Execute("Lines");
        Assert.All(vm.TrendSeries, x => Assert.IsType<LiveChartsCore.SkiaSharpView.LineSeries<double>>(x));
        vm.SetTrendStyleCommand.Execute("Columns");
        Assert.All(vm.TrendSeries, x => Assert.IsType<LiveChartsCore.SkiaSharpView.ColumnSeries<double>>(x));
    });

    [Fact]
    public void Reports_CompareMonthsCategoryByCategory() => WithApp(app =>
    {
        var compare = app.Get<ReportsViewModel>().Compare;
        compare.SetKindCommand.Execute("Months");

        Assert.True(compare.IsMonths);
        Assert.Equal(24, compare.Picks.Count);
        Assert.Equal(3, compare.Picks.Count(p => p.IsSelected));
        Assert.True(compare.HasData);
        Assert.Equal(3, compare.Totals.Count);

        compare.Picks.First(p => p.IsSelected).IsSelected = false;
        Assert.Equal(2, compare.Totals.Count);
    });

    [Fact]
    public void Reports_ListPlacesWithTheirTotals_ForThePickedPeriod() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        vm.IsYear = false;                                    // the month: the table follows the period of the report
        var places = vm.Places;

        Assert.True(places.HasRows);
        Assert.Contains(places.Rows, r => r.Name == "Lewiatan");
        var lewiatan = places.Rows.First(r => r.Name == "Lewiatan");
        Assert.Equal(54.30m, lewiatan.Amount);

        places.Search = "orl";
        Assert.Single(places.Rows);
        Assert.Equal("Orlen", places.Rows[0].Name);
        places.Search = "";

        places.SetTypeCommand.Execute("Income");
        Assert.Contains(places.Rows, r => r.Name == "Firma");
        Assert.DoesNotContain(places.Rows, r => r.Name == "Lewiatan");

        // the period of the report drives the table until the dates are edited by hand
        places.SetTypeCommand.Execute("Expense");
        vm.PreviousPeriod();
        Assert.Contains(places.Rows, r => r.Name == "Lewiatan" && r.Amount == 640m);
    });

    [Fact]
    public void Reports_AverageOfACategoryPerWeek() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        vm.AvgFrom = DateTime.Today.AddDays(-27);
        vm.AvgTo = DateTime.Today;
        Assert.NotEqual("—", vm.AvgPerWeek);
        var all = vm.AvgPerWeek;

        vm.AvgCategory = vm.AvgChoices.First(c => c.Name == "Rachunki");
        Assert.NotEqual(all, vm.AvgPerWeek);
        Assert.NotEqual("—", vm.AvgTotal);

        vm.SetAvgTypeCommand.Execute("Income");
        Assert.True(vm.AvgIsIncome);
        Assert.Null(vm.AvgCategory?.Id);              // "all" again: the expense category does not exist for income
    });

    [Fact]
    public void ReportsView_RendersTheComparisonAndPlacesTabs() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();

        vm.SetTabCommand.Execute("Compare");
        var compareView = new ReportsView { DataContext = vm };
        UiHost.Layout(compareView, 1100, 1000);
        var compareTexts = UiHost.Texts(compareView);
        UiHost.Render(compareView, 1100, 1000, "reports-compare");
        Assert.Contains("Lata do porównania:", compareTexts);
        Assert.DoesNotContain("Sumy według miejsc w wybranym okresie", compareTexts);   // only the chosen tab is shown
        Assert.DoesNotContain(compareTexts, t => t.StartsWith("Sposób liczenia"));

        vm.SetTabCommand.Execute("Places");
        var placesView = new ReportsView { DataContext = vm };
        UiHost.Layout(placesView, 1100, 900);
        var placesTexts = UiHost.Texts(placesView);
        UiHost.Render(placesView, 1100, 900, "reports-places");
        Assert.Contains(placesTexts, t => t.Contains("Lewiatan"));
        Assert.DoesNotContain("Lata do porównania:", placesTexts);
    });

    // ----- settings -----

    [Fact]
    public void Settings_ChangeTheDataFolder_CopyTheJournals_AndAskForARestart() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        var dialogs = (FakeDialogs)app.Get<IFileDialogs>();
        var restarter = (FakeRestarter)app.Get<IAppRestarter>();
        var target = Path.Combine(Path.GetTempPath(), "budzet-ui-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            // the picker was cancelled: nothing happens
            dialogs.NextFolder = null;
            vm.ChangeDataFolderCommand.Execute(null);
            Assert.False(vm.HasPendingDataFolder);

            // the folder in use is not "a new one"
            dialogs.NextFolder = vm.DataFolder;
            vm.ChangeDataFolderCommand.Execute(null);
            Assert.False(vm.HasPendingDataFolder);
            Assert.NotEmpty(vm.FolderMessage);

            dialogs.NextFolder = target;
            vm.ChangeDataFolderCommand.Execute(null);
            Assert.True(vm.HasPendingDataFolder);
            Assert.True(vm.CanApplyDataFolder);
            Assert.True(vm.CopyJournals);                 // an empty folder: the existing data goes along
            Assert.NotEmpty(vm.PendingChecks);

            vm.ApplyDataFolderCommand.Execute(null);
            Assert.Contains(Directory.GetFiles(target), f => Path.GetFileName(f).StartsWith("journal_"));
            Assert.Equal(target, app.Get<SettingsStore>().Current.DataFolder);
            Assert.True(app.Get<SettingsStore>().Current.ResetLocalDatabase);
            Assert.True(vm.RestartNeeded);
            Assert.False(vm.HasPendingDataFolder);

            vm.RestartNowCommand.Execute(null);
            Assert.Equal(1, restarter.Restarts);
        }
        finally
        {
            Directory.Delete(target, true);
        }
    });

    [Fact]
    public void Settings_RefuseAFolderThatDoesNotExist() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        var dialogs = (FakeDialogs)app.Get<IFileDialogs>();
        dialogs.NextFolder = Path.Combine(Path.GetTempPath(), "budzet-missing-" + Guid.NewGuid().ToString("N"));
        vm.ChangeDataFolderCommand.Execute(null);
        Assert.True(vm.HasPendingDataFolder);
        Assert.False(vm.CanApplyDataFolder);
        vm.ApplyDataFolderCommand.Execute(null);
        Assert.False(vm.RestartNeeded);
    });

    [Fact]
    public void Categories_OfferAListOfIcons() => WithApp(app =>
    {
        var vm = app.Get<CategoriesViewModel>();
        Assert.True(vm.IconChoices.Count >= 60);
        Assert.Equal(vm.IconChoices.Count, vm.IconChoices.Distinct().Count());

        vm.SelectedNode = vm.Roots.First();
        vm.PickIconCommand.Execute("🚗");
        Assert.Equal("🚗", vm.EditIcon);
        vm.PickIconCommand.Execute("");
        Assert.Equal("", vm.EditIcon);
    });

    // ----- window placement -----

    [Fact]
    public void WindowPlacement_KeepsAReachableWindowAsItWas()
    {
        var screen = new Rect(0, 0, 1920, 1080);
        var fitted = WindowPlacement.Fit(new Rect(200, 100, 1200, 800), screen, new Size(900, 600));
        Assert.Equal(new Rect(200, 100, 1200, 800), fitted);
    }

    [Fact]
    public void WindowPlacement_BringsAWindowBackFromAnUnpluggedMonitor()
    {
        var screen = new Rect(0, 0, 1920, 1080);
        Assert.Null(WindowPlacement.Fit(new Rect(3000, 100, 1200, 800), screen, new Size(900, 600)));    // fully off-screen
        var partly = WindowPlacement.Fit(new Rect(1700, -300, 1200, 800), screen, new Size(900, 600));
        Assert.NotNull(partly);
        Assert.True(partly!.Value.Top >= 0);                                                              // the title bar stays reachable
        Assert.True(partly.Value.Left <= 1920 - 120);
    }

    [Fact]
    public void WindowPlacement_ShrinksAWindowLargerThanTheScreen_AndRespectsTheMinimum()
    {
        var screen = new Rect(0, 0, 1366, 768);
        var big = WindowPlacement.Fit(new Rect(0, 0, 3000, 2000), screen, new Size(900, 600));
        Assert.Equal(1366, big!.Value.Width);
        Assert.Equal(768, big.Value.Height);

        var small = WindowPlacement.Fit(new Rect(100, 100, 500, 300), screen, new Size(900, 600));
        Assert.Equal(900, small!.Value.Width);
        Assert.Equal(600, small.Value.Height);

        Assert.Null(WindowPlacement.Fit(new Rect(0, 0, 10, 10), screen, new Size(900, 600)));              // nonsense is ignored
    }
}
