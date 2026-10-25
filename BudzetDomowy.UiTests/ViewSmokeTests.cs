using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;

namespace BudzetDomowy.UiTests;

/// <summary>Builds each screen from the real XAML and view model, lays it out and draws it, without ever showing a window.</summary>
public class ViewSmokeTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    [Fact]
    public void TransactionsView_ListsTheSampleRows() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.ClearFiltersCommand.Execute(null);
        vm.SetModeCommand.Execute("Range");   // the sample data spans three months; the default is the current month only
        vm.From = DateTime.Today.AddMonths(-4);
        vm.To = DateTime.Today.AddDays(1);
        vm.Reload();
        var view = new TransactionsView { DataContext = vm };
        UiHost.Layout(view, 1080, 640);
        var texts = UiHost.Texts(view); // read before the render detaches the view (the grid then releases its rows)
        UiHost.Render(view, 1080, 640, "transactions");

        Assert.Equal(9, vm.Rows.Count);
        Assert.Contains(texts, t => t.Contains("Lewiatan"));
        Assert.Contains(texts, t => t.Contains("Orlen"));
        Assert.Contains(texts, t => t.StartsWith("Wszystkie pasujące: 9"));
    });

    [Fact]
    public void EventsView_ShowsTheSelectedEventWithItsReport() => WithApp(app =>
    {
        var vm = app.Get<EventsViewModel>();
        vm.Selected = vm.Items.First();
        var view = new EventsView { DataContext = vm };
        UiHost.Layout(view, 1080, 900);
        UiHost.Render(view, 1080, 900, "events");

        Assert.Contains("Wakacje nad morzem", UiHost.Texts(view));
        Assert.True(vm.HasSlices);
        Assert.Contains(vm.Slices, s => s.Name == "Rozrywka");
    });

    [Fact]
    public void SettingsView_Builds_WithTheCategoryTree() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        var view = new SettingsView { DataContext = vm };
        UiHost.Layout(view, 1080, 640);
        UiHost.Render(view, 1080, 640, "settings");
        Assert.Contains("Dane", UiHost.Texts(view));
        Assert.NotEmpty(vm.Categories.Roots);
    });

    [Fact]
    public void DashboardView_ShowsTilesBudgetsRecentAndTheActiveEvent() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var view = new DashboardView { DataContext = vm };
        UiHost.Layout(view, 1100, 1000);
        var texts = UiHost.Texts(view);
        UiHost.Render(view, 1100, 1000, "dashboard");

        Assert.Contains("Przychody", texts);
        Assert.Contains("Wydatki", texts);
        Assert.True(vm.HasSlices);
        Assert.True(vm.HasBudgets);
        Assert.True(vm.HasActiveEvent);
        Assert.Contains(vm.BudgetBars, b => b.Path == "Rozrywka" && b.Status == "Over");   // 220 of 200
        Assert.Contains(texts, t => t.Contains("Wakacje nad morzem"));
        Assert.Contains(vm.Recent, r => r.Place == "Lewiatan");
    });

    [Fact]
    public void Dashboard_DrillsIntoSubcategoriesAndBack() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var auto = vm.Slices.First(s => s.Name == "Auto" || s.Name.StartsWith("Auto"));
        if (!auto.CanDrill)
        {
            // the sample has only a direct "Auto" posting, so there is nothing below it
            Assert.False(auto.CanDrill);
            return;
        }
        vm.DrillIntoCommand.Execute(auto);
        Assert.True(vm.CanGoBack);
        vm.BackCommand.Execute(null);
        Assert.False(vm.CanGoBack);
    });

    [Fact]
    public void Dashboard_MonthSwitchChangesTheFigures() => WithApp(app =>
    {
        var vm = app.Get<DashboardViewModel>();
        var thisMonth = vm.ExpenseText;
        vm.PreviousMonthCommand.Execute(null);
        Assert.NotEqual(thisMonth, vm.ExpenseText);          // last month had 640 zł of groceries
        Assert.Contains("640", vm.ExpenseText.Replace(" ", "").Replace("\u00A0", ""));
        vm.NextMonthCommand.Execute(null);
        Assert.Equal(thisMonth, vm.ExpenseText);
    });

    [Fact]
    public void BudgetsView_ListsCategoriesWithTheirLimits_AndSavesAnEditedLimit() => WithApp(app =>
    {
        var vm = app.Get<BudgetsViewModel>();
        var view = new BudgetsView { DataContext = vm };
        UiHost.Layout(view, 1100, 900);
        UiHost.Render(view, 1100, 900, "budgets");

        var food = vm.Rows.First(r => r.Name == "Spożywcze");
        Assert.Equal("800", food.LimitText);
        Assert.Equal("Ok", food.Status);

        food.LimitText = "1 000,50";
        Assert.Equal("1000,5", vm.Rows.First(r => r.Name == "Spożywcze").LimitText);
        Assert.Equal(1000.5m, app.Core.Reader.GetBudgets().First(b => b.CategoryId == food.CategoryId && b.ValidFromMonth == vm.Month).MonthlyAmount);

        food.LimitText = "abc"; // rejected: the old value comes back
        Assert.Equal("1000,5", food.LimitText);
        food.LimitText = "";    // empty switches the limit off for this month
        Assert.False(vm.Rows.First(r => r.Name == "Spożywcze").HasLimit);
    });

    [Fact]
    public void ReportsView_ShowsComparisonAveragesAndTheTrend() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1100, 1500);
        var texts = UiHost.Texts(view);
        UiHost.Render(view, 1100, 1500, "reports");

        Assert.Equal(3, vm.Comparison.Count);
        Assert.True(vm.HasBreakdown);
        Assert.NotEmpty(vm.TopCategories);
        Assert.True(vm.HasEvents);
        Assert.Contains(texts, t => t.StartsWith("Sposób liczenia"));
        Assert.NotEqual("—", vm.AvgPerMonth);

        vm.IsYear = true;
        Assert.Equal(DateTime.Today.Year.ToString(), vm.Title);
        vm.AvgProrate = false;
        Assert.Contains("pełnych", vm.AvgMethod);
    });

    [Fact]
    public void RecurringView_ShowsTheEntriesAsAPageOfItsOwn() => WithApp(app =>
    {
        var vm = app.Get<MainViewModel>();
        vm.Navigate("recurring");
        var page = Assert.IsType<RecurringViewModel>(vm.CurrentPage);
        Assert.Single(page.Items);

        var recurring = new RecurringView { DataContext = page };
        page.Selected = page.Items[0];
        UiHost.Layout(recurring, 1050, 650);
        UiHost.Render(recurring, 1050, 650, "recurring");
        Assert.Equal("1200", page.EditAmount);
    });

    [Fact]
    public void MainWindow_Builds_WithoutBeingShown() => WithApp(app =>
    {
        var vm = app.Get<MainViewModel>();
        var window = new MainWindow(vm, app.Get<SettingsStore>());
        var content = (System.Windows.FrameworkElement)window.Content;
        UiHost.Layout(content, 1280, 800);
        window.Content = null;
        content.DataContext = vm; // the window handed its data context down; keep it once the content is detached
        UiHost.Render(content, 1280, 800, "main");

        Assert.False(window.IsVisible);
        Assert.Contains(UiHost.Texts(content), t => t == t.ToUpperInvariant() && t.Contains("202")); // the big date
    });
}
