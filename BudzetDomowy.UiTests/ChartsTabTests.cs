using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;

namespace BudzetDomowy.UiTests;

/// <summary>Reports → "Wykresy": lines for chosen categories over a range set by dates and by a bar with two handles.</summary>
public class ChartsTabTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() => test(app));
    }

    private static DateOnly D(string s) => DateOnly.Parse(s);

    private static Guid Cat(TestApp app, string name, string? parent = null) =>
        app.Core.Reader.GetCategories().First(c => c.Name == name && (parent is null ? c.ParentId is null : c.ParentId is not null)).Id;

    private static void Add(TestApp app, string date, decimal amount, Guid category, TransactionType type = TransactionType.Expense) =>
        app.Core.Transactions.Add(D(date), type, amount, category, "n", null, place: "p");

    /// <summary>Data from 2024-02-10 to 2025-12-31: groceries (with a subcategory), fuel, electricity, and one salary on the last day.</summary>
    private static ChartsViewModel Setup(TestApp app, bool open = true)
    {
        Add(app, "2024-02-10", 80m, Cat(app, "Prąd", "Rachunki"));
        Add(app, "2025-01-05", 100m, Cat(app, "Supermarket", "Spożywcze"));
        Add(app, "2025-01-20", 50m, Cat(app, "Spożywcze"));
        Add(app, "2025-02-05", 200m, Cat(app, "Supermarket", "Spożywcze"));
        Add(app, "2025-01-15", 100m, Cat(app, "Paliwo", "Auto"));
        Add(app, "2025-03-15", 300m, Cat(app, "Paliwo", "Auto"));
        Add(app, "2025-12-31", 1000m, Cat(app, "Wynagrodzenie"), TransactionType.Income);
        var vm = new ChartsViewModel(app.Core) { RebuildDelay = TimeSpan.Zero };
        if (open) vm.EnsureFresh();
        return vm;
    }

    private static double[] Values(ISeries series) => ((LineSeries<double>)series).Values!.ToArray();

    // ----- the points of the lines -----

    [Fact]
    public void TheBuckets_AreWeeksFromMonday_Months_AndYears()
    {
        Assert.Equal(D("2025-03-10"), ChartBuckets.Start(D("2025-03-16"), ChartStep.Week));          // a Sunday belongs to the week that began on Monday the 10th
        Assert.Equal(D("2025-03-17"), ChartBuckets.Start(D("2025-03-17"), ChartStep.Week));
        Assert.Equal(D("2025-03-01"), ChartBuckets.Start(D("2025-03-31"), ChartStep.Month));
        Assert.Equal(D("2025-01-01"), ChartBuckets.Start(D("2025-12-31"), ChartStep.Year));

        Assert.Equal(23, ChartBuckets.Starts(D("2024-02-10"), D("2025-12-31"), ChartStep.Month).Count);
        Assert.Equal(2, ChartBuckets.Starts(D("2024-02-10"), D("2025-12-31"), ChartStep.Year).Count);
        Assert.Equal(2, ChartBuckets.Starts(D("2025-03-16"), D("2025-03-17"), ChartStep.Week).Count);    // Sunday and Monday: two weeks
        Assert.Empty(ChartBuckets.Starts(D("2025-03-16"), D("2025-03-15"), ChartStep.Month));
        Assert.Equal("2025", ChartBuckets.Label(D("2025-01-01"), ChartStep.Year));
        Assert.Equal("10.03.25", ChartBuckets.Label(D("2025-03-10"), ChartStep.Week));
    }

    // ----- the range -----

    [Fact]
    public void TheRange_StartsAsAllTheData_AndTheBarSpansFromTheFirstEntryToTheLast() => WithApp(app =>
    {
        var vm = Setup(app);

        Assert.True(vm.HasData);
        Assert.Equal((D("2024-02-10"), D("2025-12-31")), (vm.MinDate, vm.MaxDate));
        Assert.Equal((D("2024-02-10").DayNumber, D("2025-12-31").DayNumber), ((int)vm.RangeMin, (int)vm.RangeMax));
        Assert.Equal((D("2024-02-10"), D("2025-12-31")), (vm.From, vm.To));
        Assert.Equal((vm.RangeMin, vm.RangeMax), (vm.LowerDay, vm.UpperDay));
        Assert.Equal(("10.02.2024", "31.12.2025"), (vm.MinText, vm.MaxText));
        Assert.Equal("Zakres: 691 dni", vm.RangeText);
    });

    [Fact]
    public void TheDatesAndTheHandles_FollowEachOther_AndStayInsideTheData() => WithApp(app =>
    {
        var vm = Setup(app);

        vm.FromDate = new DateTime(2025, 1, 1);                                              // typed
        Assert.Equal((double)D("2025-01-01").DayNumber, vm.LowerDay);
        vm.UpperDay = D("2025-06-30").DayNumber;                                             // dragged
        Assert.Equal(new DateTime(2025, 6, 30), vm.ToDate);
        Assert.Equal((D("2025-01-01"), D("2025-06-30")), (vm.From, vm.To));

        vm.FromDate = new DateTime(2020, 1, 1);                                              // before the first entry: pulled back to it
        Assert.Equal(D("2024-02-10"), vm.From);
        Assert.Equal(new DateTime(2024, 2, 10), vm.FromDate);
        vm.ToDate = new DateTime(2030, 1, 1);                                                // after the last one
        Assert.Equal(D("2025-12-31"), vm.To);

        vm.FromDate = new DateTime(2025, 11, 1);
        vm.ToDate = new DateTime(2025, 5, 1);                                                // the end before the start: the start comes along
        Assert.Equal((D("2025-05-01"), D("2025-05-01")), (vm.From, vm.To));
        vm.FromDate = new DateTime(2025, 8, 1);                                              // the start after the end: the end comes along
        Assert.Equal((D("2025-08-01"), D("2025-08-01")), (vm.From, vm.To));

        vm.FromDate = null;                                                                  // an emptied field means "from the first entry"
        Assert.Equal(D("2024-02-10"), vm.From);
        Assert.Equal(new DateTime(2024, 2, 10), vm.FromDate);
    });

    [Fact]
    public void ARangeThatIsChanged_RedrawsTheLines() => WithApp(app =>
    {
        var vm = Setup(app);
        Assert.Equal(23, ((LineSeries<double>)vm.Series.First()).Values!.Count());

        vm.SetRange(D("2025-01-01"), D("2025-03-31"));

        Assert.Equal(3, ((LineSeries<double>)vm.Series.First()).Values!.Count());
        Assert.Equal(["sty 25", "lut 25", "mar 25"], ((LiveChartsCore.SkiaSharpView.Axis)vm.XAxes.First()).Labels!.Select(l => l.ToLowerInvariant().Replace(".", "")));
    });

    [Fact]
    public void OnlyAboutADozenLabelsAreWritten_SoLongRangesStayReadable() => WithApp(app =>
    {
        var vm = Setup(app);                                                                 // 23 months

        var labels = ((Axis)vm.XAxes.First()).Labels!;
        Assert.Equal(23, labels.Count);                                                      // every point keeps its place
        Assert.Equal(12, labels.Count(l => l.Length > 0));                                   // every second one has a label
        Assert.NotEqual("", labels[0]);
        Assert.Equal("", labels[1]);

        vm.SetRange(D("2025-01-01"), D("2025-06-30"));
        Assert.All(((Axis)vm.XAxes.First()).Labels!, l => Assert.NotEqual("", l));           // a short range: every label
    });

    // ----- the lines -----

    [Fact]
    public void TheFirstTimeTheBiggestMainCategoriesAreThere_ForTheWholeRange() => WithApp(app =>
    {
        var vm = Setup(app);

        Assert.Equal(["Auto", "Spożywcze", "Rachunki"], vm.Chips.Select(c => c.Name));        // 400, 350, 80
        Assert.Equal(["Auto", "Spożywcze", "Rachunki"], vm.Series.Select(s => s.Name));
        Assert.Equal(3, vm.Chips.Select(c => c.Color).Distinct().Count());
        Assert.False(vm.ShowHint);
    });

    [Fact]
    public void ALineSumsTheCategoryWithItsSubcategories_PerMonth() => WithApp(app =>
    {
        var vm = Setup(app);
        var groceries = Values(vm.Series.Single(s => s.Name == "Spożywcze"));
        var fuel = Values(vm.Series.Single(s => s.Name == "Auto"));

        Assert.Equal(23, groceries.Length);
        Assert.Equal(150d, groceries[11]);                                                   // January 2025: 100 in the subcategory and 50 in the category itself
        Assert.Equal(200d, groceries[12]);
        Assert.Equal(350d, groceries.Sum());
        Assert.Equal(100d, fuel[11]);
        Assert.Equal(300d, fuel[13]);                                                        // March 2025
        Assert.Equal(400d, fuel.Sum());
        Assert.Equal("350 zł", vm.Chips.Single(c => c.Name == "Spożywcze").TotalText.Replace(' ', ' '));
    });

    [Fact]
    public void ASubcategory_HasALineOfItsOwn_AndOnlyItsOwnEntriesAreOnIt() => WithApp(app =>
    {
        var vm = Setup(app);
        vm.ClearChipsCommand.Execute(null);

        Assert.True(vm.AddCategory(Cat(app, "Supermarket", "Spożywcze")));

        var chip = Assert.Single(vm.Chips);
        Assert.Equal("Spożywcze › Supermarket", chip.Name);
        Assert.Equal(300d, Values(vm.Series.Single()).Sum());                                // 100 + 200: the 50 booked in the main category is not on it
    });

    [Fact]
    public void TheStep_ChangesThePoints_NotTheTotals() => WithApp(app =>
    {
        var vm = Setup(app);

        vm.SetStepCommand.Execute("Year");
        Assert.True(vm.IsYearStep);
        var byYear = Values(vm.Series.Single(s => s.Name == "Auto"));
        Assert.Equal([0d, 400d], byYear);                                                    // 2024, 2025

        vm.SetStepCommand.Execute("Week");
        var byWeek = Values(vm.Series.Single(s => s.Name == "Auto"));
        Assert.Equal(ChartBuckets.Starts(D("2024-02-10"), D("2025-12-31"), ChartStep.Week).Count, byWeek.Length);
        Assert.Equal(400d, byWeek.Sum());
        Assert.Contains("tygodni", vm.PointsText);

        vm.SetStepCommand.Execute("Month");
        Assert.Contains("miesięcy", vm.PointsText);
        Assert.Contains("23", vm.PointsText);
    });

    [Fact]
    public void IncomeCategoriesCountIncomes_AndCategoriesOfBothKindsCountBoth() => WithApp(app =>
    {
        var vm = Setup(app);
        var both = app.Core.Categories.Add("Wspólna", null, CategoryType.Both).Id;
        Add(app, "2025-04-01", 25m, both);
        Add(app, "2025-04-02", 75m, both, TransactionType.Income);
        vm.Reload();
        vm.ClearChipsCommand.Execute(null);

        vm.AddCategory(Cat(app, "Wynagrodzenie"));
        vm.AddCategory(both);

        Assert.Equal(1000d, Values(vm.Series.Single(s => s.Name == "Wynagrodzenie")).Sum());
        Assert.Equal(100d, Values(vm.Series.Single(s => s.Name == "Wspólna")).Sum());
    });

    [Fact]
    public void TheQuickChoice_PutsInTheBiggestOnesOfTheCurrentRange_AndReplacesWhatWasThere() => WithApp(app =>
    {
        var vm = Setup(app);
        vm.SetRange(D("2025-02-01"), D("2025-03-31"));                                       // February and March 2025 only

        vm.AddTop(5, TransactionType.Expense);
        Assert.Equal(["Auto", "Spożywcze"], vm.Chips.Select(c => c.Name));                   // 300 against 200; Rachunki is outside this range

        vm.AddTop(1, TransactionType.Expense);
        Assert.Equal(["Auto"], vm.Chips.Select(c => c.Name));

        vm.AddTop(5, TransactionType.Income);                                                // no income in these two months
        Assert.Empty(vm.Chips);
        Assert.True(vm.ShowHint);

        vm.SetRange(D("2025-12-01"), D("2025-12-31"));
        vm.AddTop(5, TransactionType.Income);
        Assert.Equal(["Wynagrodzenie"], vm.Chips.Select(c => c.Name));
    });

    // ----- the tags -----

    [Fact]
    public void PickingACategory_AddsATag_AndTheBoxIsEmptyAgain() => WithApp(app =>
    {
        var vm = Setup(app);
        vm.ClearChipsCommand.Execute(null);
        var pick = Cat(app, "Rozrywka");

        vm.PickedCategory = pick;

        Assert.Equal(["Rozrywka"], vm.Chips.Select(c => c.Name));
        Assert.Null(vm.PickedCategory);                                                      // ready for the next one
        vm.PickedCategory = pick;                                                            // the same again: nothing is doubled
        Assert.Single(vm.Chips);
        Assert.False(vm.AddCategory(Guid.NewGuid()));
        Assert.True(vm.Chips[0].HasChildren);
    });

    [Fact]
    public void ATagCanBeRemoved_OrReplacedByItsSubcategories() => WithApp(app =>
    {
        var vm = Setup(app);
        var groceries = vm.Chips.Single(c => c.Name == "Spożywcze");

        vm.SplitChipCommand.Execute(groceries);

        Assert.DoesNotContain(vm.Chips, c => c.Name == "Spożywcze");
        Assert.Equal(["Spożywcze › Supermarket", "Spożywcze › Piekarnia", "Spożywcze › Mięso i wędliny", "Spożywcze › Owoce i warzywa"],
            vm.Chips.Where(c => c.Name.StartsWith("Spożywcze")).Select(c => c.Name));
        Assert.Equal(6, vm.Chips.Count);                                                     // Auto, Rachunki and the four
        Assert.Equal(vm.Chips.Count, vm.Chips.Select(c => c.Color).Distinct().Count());      // every line has a colour of its own, also the subcategories of one parent
        Assert.Equal(vm.Chips.Count, vm.Series.Count());

        var leaf = vm.Chips.Single(c => c.Name == "Spożywcze › Piekarnia");
        vm.SplitChipCommand.Execute(leaf);                                                   // nothing below it: nothing happens
        Assert.Contains(leaf, vm.Chips);

        vm.RemoveChipCommand.Execute(leaf);
        Assert.DoesNotContain(leaf, vm.Chips);
        Assert.Equal(5, vm.Series.Count());
    });

    [Fact]
    public void TheChartHoldsAtMostFifteenLines_AndSaysSo() => WithApp(app =>
    {
        var vm = Setup(app);
        vm.ClearChipsCommand.Execute(null);
        foreach (var c in app.Core.Reader.GetCategories().Where(c => !c.IsArchived).Take(15)) Assert.True(vm.AddCategory(c.Id));

        var sixteenth = app.Core.Reader.GetCategories().Where(c => !c.IsArchived).Skip(15).First();
        Assert.False(vm.AddCategory(sixteenth.Id));

        Assert.Equal(15, vm.Chips.Count);
        Assert.Equal("Na wykresie może być najwyżej 15 kategorii naraz: usuń którąś, żeby dodać następną.", vm.Status);
        vm.RemoveChipCommand.Execute(vm.Chips[0]);
        Assert.True(vm.AddCategory(sixteenth.Id));
        Assert.Equal("", vm.Status);
    });

    // ----- new data -----

    [Fact]
    public void WhenDataIsAdded_TheChoiceStays_AndAnEndThatSatAtTheEndOfTheDataFollowsIt() => WithApp(app =>
    {
        var vm = Setup(app);
        vm.SetRange(D("2025-01-01"), D("2025-12-31"));                                       // the right end is at the last entry; the left one is not at the first
        vm.RemoveChipCommand.Execute(vm.Chips.First(c => c.Name == "Auto"));

        Add(app, "2026-02-14", 60m, Cat(app, "Supermarket", "Spożywcze"));
        vm.Reload();

        Assert.Equal(D("2026-02-14"), vm.MaxDate);
        Assert.Equal((D("2025-01-01"), D("2026-02-14")), (vm.From, vm.To));
        Assert.Equal(["Spożywcze", "Rachunki"], vm.Chips.Select(c => c.Name));               // what was removed did not come back
        Assert.Equal(14, Values(vm.Series.First()).Length);                                  // January 2025 to February 2026

        vm.SetRange(D("2025-03-01"), D("2025-06-30"));                                       // both ends moved by hand
        Add(app, "2026-03-01", 10m, Cat(app, "Supermarket", "Spożywcze"));
        vm.Reload();
        Assert.Equal((D("2025-03-01"), D("2025-06-30")), (vm.From, vm.To));
    });

    [Fact]
    public void WithoutData_ThereIsNothingToDrawAndTheTabSaysSo() => WithApp(app =>
    {
        var vm = new ChartsViewModel(app.Core);
        vm.EnsureFresh();

        Assert.False(vm.HasData);
        Assert.True(vm.NoData);
        Assert.Empty(vm.Series);
        Assert.Empty(vm.Chips);
        vm.FromDate = new DateTime(2025, 1, 1);                                              // nothing to clamp to: ignored
        vm.AddTop(5, TransactionType.Expense);
        Assert.Empty(vm.Chips);
    });

    [Fact]
    public void TheReportsReadTheChartsOnlyWhenTheirTabIsShown() => WithApp(app =>
    {
        Add(app, "2025-01-05", 100m, Cat(app, "Auto"));
        var reports = app.Get<ReportsViewModel>();
        Assert.True(reports.Charts.Stale);                                                   // not read at the start of the program
        Assert.False(reports.ShowPeriod == false && reports.IsOverview);

        reports.TabIndex = (int)ReportTab.Charts;

        Assert.True(reports.IsCharts);
        Assert.False(reports.ShowPeriod);                                                    // the charts have a range of their own
        Assert.False(reports.Charts.Stale);
        Assert.True(reports.Charts.HasData);
        Assert.Equal(["Auto"], reports.Charts.Chips.Select(c => c.Name));

        reports.TabIndex = (int)ReportTab.Overview;
        Assert.True(reports.ShowPeriod);
    });

    // ----- the bar with two handles -----

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static RangeSlider NewSlider(double width = 500)
    {
        var slider = new RangeSlider { Minimum = 0, Maximum = 100, LowerValue = 0, UpperValue = 100 };
        UiHost.Layout(slider, width, 34);
        return slider;
    }

    [Fact]
    public void TheHandles_SitAtTheEndsOfTheBar_AndNeitherPassesTheOther() => WithApp(app =>
    {
        var slider = NewSlider();
        Assert.Equal(0, slider.LowerHandleLeft);
        Assert.Equal(480, slider.UpperHandleLeft);                                           // the bar is 500 wide, a handle 20

        slider.LowerValue = 50;
        Assert.Equal(240, slider.LowerHandleLeft);
        slider.UpperValue = 20;                                                              // below the lower one: stops there
        Assert.Equal(50, slider.UpperValue);
        slider.LowerValue = 80;
        Assert.Equal(50, slider.LowerValue);
        slider.LowerValue = -30;
        Assert.Equal(0, slider.LowerValue);
        slider.UpperValue = 1000;
        Assert.Equal(100, slider.UpperValue);
        slider.LowerValue = 12.6;                                                            // whole numbers only: whole days
        Assert.Equal(13, slider.LowerValue);

        slider.Maximum = 40;                                                                 // a shorter bar takes the upper handle with it
        Assert.Equal(40, slider.UpperValue);
    });

    [Fact]
    public void DraggingAHandle_MovesItByTheShareOfTheBar_AndTheKeyboardByDaysMonthsAndYears() => WithApp(app =>
    {
        var slider = NewSlider();

        slider.LowerHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        slider.LowerHandle.RaiseEvent(new DragDeltaEventArgs(96, 0) { RoutedEvent = Thumb.DragDeltaEvent });                 // 96 of 480 pixels: a fifth of 100
        Assert.Equal(20, slider.LowerValue);
        slider.LowerHandle.RaiseEvent(new DragDeltaEventArgs(48, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Assert.Equal(30, slider.LowerValue);

        slider.UpperHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        slider.UpperHandle.RaiseEvent(new DragDeltaEventArgs(-480, 0) { RoutedEvent = Thumb.DragDeltaEvent });               // far to the left: stops at the lower handle
        Assert.Equal(30, slider.UpperValue);

        void Press(Thumb thumb, Key key) => thumb.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        slider.UpperValue = 60;
        Press(slider.UpperHandle, Key.Right);
        Assert.Equal(61, slider.UpperValue);
        Press(slider.UpperHandle, Key.Left);
        Press(slider.UpperHandle, Key.Left);
        Assert.Equal(59, slider.UpperValue);
        Press(slider.UpperHandle, Key.PageUp);
        Assert.Equal(89, slider.UpperValue);
        Press(slider.UpperHandle, Key.End);
        Assert.Equal(100, slider.UpperValue);
        Press(slider.LowerHandle, Key.Home);
        Assert.Equal(0, slider.LowerValue);
    });

    [Fact]
    public void TheHandlesShowTheirDateInAToolTip() => WithApp(app =>
    {
        var slider = new RangeSlider
        {
            Minimum = D("2024-01-01").DayNumber, Maximum = D("2025-12-31").DayNumber, ToolTipConverter = new DayNumberToDateConverter(),
        };
        slider.LowerValue = D("2024-06-15").DayNumber;
        slider.UpperValue = D("2025-03-01").DayNumber;
        UiHost.Layout(slider, 600, 34);

        Assert.Equal("15.06.2024", slider.LowerHandle.ToolTip);
        Assert.Equal("01.03.2025", slider.UpperHandle.ToolTip);
    });

    // ----- the tab -----

    [Fact]
    public void TheTab_ShowsTheRangeTheTagsAndTheChart() => WithApp(app =>
    {
        Setup(app, open: false);
        var reports = app.Get<ReportsViewModel>();
        var view = new ReportsView { DataContext = reports };
        reports.Charts.RebuildDelay = TimeSpan.Zero;
        reports.TabIndex = (int)ReportTab.Charts;
        UiHost.Layout(view, 1250, 1100);

        var tabs = UiHost.Descendants<TabControl>(view).First();
        Assert.Contains("Wykresy", tabs.Items.Cast<TabItem>().Select(t => t.Header?.ToString()));

        var from = Assert.IsType<DatePicker>(view.FindName("ChartFromBox"));
        var to = Assert.IsType<DatePicker>(view.FindName("ChartToBox"));
        Assert.Equal((new DateTime(2024, 2, 10), new DateTime(2025, 12, 31)), (from.SelectedDate, to.SelectedDate));
        var range = Assert.IsType<RangeSlider>(view.FindName("ChartRange"));
        Assert.Equal((D("2024-02-10").DayNumber, D("2025-12-31").DayNumber), ((int)range.Minimum, (int)range.Maximum));
        Assert.Equal(("10.02.2024", "31.12.2025"), (((TextBlock)view.FindName("ChartMinText")!).Text, ((TextBlock)view.FindName("ChartMaxText")!).Text));

        // typing a date moves the handle; moving the handle changes the date
        from.SelectedDate = new DateTime(2025, 1, 1);
        Assert.Equal(D("2025-01-01").DayNumber, (int)range.LowerValue);
        range.UpperValue = D("2025-06-30").DayNumber;
        Assert.Equal(new DateTime(2025, 6, 30), to.SelectedDate);

        var chips = Assert.IsType<ItemsControl>(view.FindName("ChipsList"));
        Assert.Equal(3, chips.Items.Count);
        var texts = UiHost.Texts(view);
        Assert.Contains("Auto", texts);
        Assert.Contains("Spożywcze", texts);
        Assert.Contains(UiHost.Descendants<Button>(view).Where(UiHost.IsShown), b => b.Content?.ToString() == "Szybki wybór ▾");
        Assert.NotNull(view.FindName("QuickExpenses5"));
        Assert.NotNull(view.FindName("ChartCategoriesBoxPlaceholder") ?? view.FindName("ChartCategoryBox"));
        UiHost.Render(view, 1250, 1100, "charts-tab");

        // choosing a category in the box adds a tag and the box is empty again
        var box = Assert.IsType<BudzetDomowy.App.Controls.FilterComboBox>(view.FindName("ChartCategoryBox"));
        box.SelectedValue = Cat(app, "Rozrywka");
        UiHost.Pump();
        Assert.Contains("Rozrywka", reports.Charts.Chips.Select(c => c.Name));
        Assert.Null(box.SelectedValue);
        Assert.Equal("", box.Text);
        Assert.Equal(4, chips.Items.Count);
        reports.Charts.RemoveChipCommand.Execute(reports.Charts.Chips.Single(c => c.Name == "Rozrywka"));

        var buttons = UiHost.Descendants<Button>(view).Where(b => b.ToolTip?.ToString()?.StartsWith("Usuń z wykresu") == true).ToList();
        Assert.Equal(3, buttons.Count);
        buttons[0].Command.Execute(buttons[0].CommandParameter);
        Assert.Equal(2, chips.Items.Count);

        ((RadioButton)view.FindName("ChartStepYear")!).Command.Execute("Year");
        Assert.True(reports.Charts.IsYearStep);
    });

    [Fact]
    public void TheLinesAreDrawn_WithOneColourEach_FromZero_AndWithTheLegend()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var vm = Setup(app);
            var chart = new SKCartesianChart
            {
                Width = 900, Height = 420, Series = vm.Series.ToArray(), XAxes = vm.XAxes.Cast<Axis>().ToArray(), YAxes = vm.YAxes.Cast<Axis>().ToArray(),
                LegendPosition = LiveChartsCore.Measure.LegendPosition.Bottom, Background = SkiaSharp.SKColors.DarkSlateGray,
            };
            var path = Path.Combine(UiHost.OutputDir, "charts-lines.png");
            chart.SaveImage(path);
            Assert.True(new FileInfo(path).Length > 2000);
            Assert.Equal(0, ((Axis)vm.YAxes.First()).MinLimit);
        });
    }
}
