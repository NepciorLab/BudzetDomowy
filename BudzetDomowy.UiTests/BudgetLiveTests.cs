using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

public class BudgetLiveTests
{
    [Fact]
    public void Dashboard_ShowsANewLimitAtOnce_WithoutARestart()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var food = app.Core.Reader.GetCategories().First(c => c.Name == "Spożywcze").Id;
            app.Core.Transactions.Add(today, TransactionType.Expense, 50m, food, "", null, place: "Sklep");
            var dashboard = app.Get<DashboardViewModel>();
            var budgets = app.Get<BudgetsViewModel>();
            Assert.False(dashboard.HasBudgets);

            budgets.Rows.First(r => r.CategoryId == food).LimitText = "400";        // typed on the Budgets screen
            Assert.True(dashboard.HasBudgets);                                      // at once: no waiting for the timer of the hub

            Assert.True(dashboard.HasBudgets);
            Assert.Contains(dashboard.BudgetBars, b => b.Path == "Spożywcze");
        });
    }

    [Fact]
    public void Dashboard_ReadsTheLimitsAgainWhenItIsOpened_EvenIfNoNotificationCameThrough()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var food = app.Core.Reader.GetCategories().First(c => c.Name == "Spożywcze").Id;
            app.Core.Transactions.Add(today, TransactionType.Expense, 50m, food, "", null, place: "Sklep");
            app.Get<DataHub>().Dispose();                                                   // nothing refreshes the screens by itself now
            var main = app.Get<MainViewModel>();
            main.Navigate("dashboard");
            var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
            Assert.False(dashboard.HasBudgets);

            main.Navigate("budgets");
            app.Core.Budgets.SetLimit(food, 400m, today);                                  // changed behind the back of the screens
            Assert.False(dashboard.HasBudgets);                                             // not told: still the old picture …
            main.Navigate("dashboard");
            Assert.True(dashboard.HasBudgets);                                              // … until the screen is opened
            Assert.Contains(dashboard.BudgetBars, b => b.Path == "Spożywcze");
        });
    }
}
