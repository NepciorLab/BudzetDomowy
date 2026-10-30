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

    [Fact]
    public void Budgets_HaveTwoFields_TheMonthlyAndTheYearlyLimit_EachWithItsOwnBar()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var food = app.Core.Reader.GetCategories().First(c => c.Name == "Spożywcze").Id;
            app.Core.Transactions.Add(today, TransactionType.Expense, 50m, food, "", null, place: "Sklep");
            app.Core.Transactions.Add(new DateOnly(today.Year, 1, 1), TransactionType.Expense, 150m, food, "", null, place: "Sklep");   // another month of the year
            var budgets = app.Get<BudgetsViewModel>();
            var dashboard = app.Get<DashboardViewModel>();
            var row = budgets.Rows.First(r => r.CategoryId == food);
            Assert.False(row.HasLimit);
            Assert.False(row.HasYearLimit);

            row.LimitText = "400";
            Assert.True(row.HasLimit);
            Assert.False(row.HasYearLimit);                                             // one does not switch the other on
            row.YearLimitText = "1 000";
            Assert.True(row.HasYearLimit);
            Assert.Equal("1000", row.YearLimitText);
            Assert.Equal("400", row.LimitText);                                          // … nor off
            Assert.Equal(0.125, row.Fraction, 3);                                        // this month: 50 of 400
            Assert.Equal(0.2, row.YearFraction, 3);                                      // this year: 200 of 1000
            Assert.Equal("20%", row.YearPercentText);
            Assert.Equal("Ok", row.YearStatus);
            Assert.Contains("200", row.YearSpentText);
            Assert.Contains("1 000", budgets.TotalText.Replace(" ", " "));         // the total of the yearly limits is told as well
            Assert.Contains("rocznych", budgets.TotalText);

            row.YearLimitText = "";                                                      // taken off again
            Assert.False(row.HasYearLimit);
            Assert.True(row.HasLimit);
            row.YearLimitText = "250";
            Assert.Equal("Warning", row.YearStatus);                                     // 200 of 250 is 80 %

            dashboard.SetModeCommand.Execute("Year");                                    // the Dashboard measures the year by the yearly limit
            var bar = Assert.Single(dashboard.BudgetBars, b => b.Path == "Spożywcze");
            Assert.Equal("80%", bar.PercentText);
            Assert.Contains("250", bar.Text);
        });
    }

    [Fact]
    public void Budgets_CopyFromThePreviousMonth_BringsTheYearlyLimitAlong()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var food = app.Core.Reader.GetCategories().First(c => c.Name == "Spożywcze").Id;
            var budgets = app.Get<BudgetsViewModel>();
            var previous = new DateOnly(2026, 4, 1);
            app.Core.Budgets.SetYearlyLimit(food, 900m, previous);
            app.Core.Budgets.SetLimit(food, 80m, new DateOnly(2026, 5, 1));
            budgets.Month = new DateOnly(2026, 5, 1);
            var row = budgets.Rows.First(r => r.CategoryId == food);
            Assert.Equal("900", row.YearLimitText);                                     // taken over from April by itself
            Assert.Equal("80", row.LimitText);
        });
    }
}
