using System.Globalization;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

public class DashboardYearTests
{
    private static string Digits(string money) => new(money.Where(c => char.IsDigit(c) || c == ',').ToArray());

    private static void WithApp(Action<TestApp, DashboardViewModel, Func<string, Guid>> test)
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var cats = app.Core.Reader.GetCategories();
            Guid Cat(string name) => cats.First(c => c.Name == name).Id;
            var year = DateTime.Today.Year;
            // two earlier years with entries, and the present one
            app.Core.Transactions.Add(new DateOnly(year - 2, 3, 10), TransactionType.Expense, 100m, Cat("Spożywcze"), "", null, place: "A");
            app.Core.Transactions.Add(new DateOnly(year - 2, 9, 5), TransactionType.Income, 1000m, Cat("Wynagrodzenie"), "", null, place: "Firma");
            app.Core.Transactions.Add(new DateOnly(year - 1, 1, 15), TransactionType.Expense, 300m, Cat("Spożywcze"), "", null, place: "A");
            app.Core.Transactions.Add(new DateOnly(year - 1, 11, 20), TransactionType.Expense, 200m, Cat("Auto"), "", null, place: "B");
            app.Core.Transactions.Add(new DateOnly(year - 1, 6, 1), TransactionType.Income, 5000m, Cat("Wynagrodzenie"), "", null, place: "Firma");
            app.Core.Transactions.Add(new DateOnly(year, 1, 5), TransactionType.Expense, 40m, Cat("Spożywcze"), "", null, place: "A");
            test(app, app.Get<DashboardViewModel>(), Cat);
        });
    }

    [Fact]
    public void Year_ShowsTheSumsOfTheWholeYear_AndTheTitleIsTheYear() => WithApp((app, vm, cat) =>
    {
        var last = DateTime.Today.Year - 1;
        vm.Month = new DateOnly(last, 3, 1);
        Assert.False(vm.IsYear);
        Assert.Equal("0,00", Digits(vm.ExpenseText));                                      // March of last year: nothing

        vm.SetModeCommand.Execute("Year");
        Assert.True(vm.IsYear);
        Assert.False(vm.IsMonthMode);
        Assert.Equal(last.ToString(), vm.MonthTitle);
        Assert.Equal("500,00", Digits(vm.ExpenseText));                                    // 300 + 200
        Assert.Equal("5000,00", Digits(vm.IncomeText));
        Assert.True(vm.HasSlices);
        Assert.Equal(["Spożywcze", "Auto"], vm.Slices.Select(s => s.Name));                // the breakdown of the year, the larger first

        vm.SetModeCommand.Execute("Month");
        Assert.False(vm.IsYear);
        Assert.Equal("0,00", Digits(vm.ExpenseText));                                      // back to the month: the same March, still empty
    });

    [Fact]
    public void Year_ComparesAFinishedYearWithTheWholeYearBefore() => WithApp((app, vm, cat) =>
    {
        var year = DateTime.Today.Year;
        vm.SetModeCommand.Execute("Year");
        vm.Month = new DateOnly(year - 1, 12, 1);
        // expenses 500 against 100 the year before: +400 %
        Assert.Contains("400", vm.ExpenseChange);
        Assert.Contains("▲", vm.ExpenseChange);
        Assert.False(vm.HasChangeTip);                                                     // a finished year: no "the same days" explanation
    });

    [Fact]
    public void Year_RunningYearIsComparedWithTheSameDaysOfTheYearBefore() => WithApp((app, vm, cat) =>
    {
        vm.SetModeCommand.Execute("Year");
        Assert.Equal(DateTime.Today.Year.ToString(), vm.MonthTitle);
        Assert.True(vm.HasChangeTip);                                                      // "1 stycznia–… … z 1 stycznia–… …"
        Assert.Contains((DateTime.Today.Year - 1).ToString(), vm.ChangeTip);
        Assert.Equal("40,00", Digits(vm.ExpenseText));
    });

    [Fact]
    public void Year_ArrowsMoveByYears_ToTheYearsThatHaveEntries() => WithApp((app, vm, cat) =>
    {
        var year = DateTime.Today.Year;
        vm.SetModeCommand.Execute("Year");
        Assert.True(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);                                                        // the present year is the last
        vm.PreviousMonthCommand.Execute(null);
        Assert.Equal(year - 1, vm.Month.Year);
        vm.PreviousMonthCommand.Execute(null);
        Assert.Equal(year - 2, vm.Month.Year);
        Assert.False(vm.CanGoPrevious);                                                    // nothing earlier
        vm.NextPeriod();
        Assert.Equal(year - 1, vm.Month.Year);
        Assert.Equal("Poprzedni rok", vm.PreviousTip);
        vm.SetModeCommand.Execute("Month");
        Assert.Equal("Poprzedni miesiąc", vm.PreviousTip);
    });

    [Fact]
    public void Year_ThePickerListsYears_AndPickingOneChangesTheYear() => WithApp((app, vm, cat) =>
    {
        var year = DateTime.Today.Year;
        vm.SetModeCommand.Execute("Year");
        vm.TogglePickerCommand.Execute(null);
        Assert.True(vm.IsPickerOpen);
        Assert.Equal(12, vm.PickerMonths.Count);
        Assert.Equal(year, vm.PickerMonths[^1].Number);                                    // the last year of the list is the present one
        Assert.Equal($"{year - 11}–{year}", vm.PickerTitle);
        Assert.True(vm.PickerMonths.Single(p => p.Number == year - 1).HasData);
        Assert.False(vm.PickerMonths.Single(p => p.Number == year - 5).HasData);
        Assert.True(vm.PickerMonths.Single(p => p.IsCurrent).Number == year);

        vm.ShiftPickerYearCommand.Execute("-1");
        Assert.Equal($"{year - 12}–{year - 1}", vm.PickerTitle);
        vm.PickMonthCommand.Execute(year - 2);
        Assert.False(vm.IsPickerOpen);
        Assert.Equal(year - 2, vm.Month.Year);
        Assert.Equal(year - 2, int.Parse(vm.MonthTitle, CultureInfo.InvariantCulture));

        vm.SetModeCommand.Execute("Month");
        vm.IsPickerOpen = true;                                                            // (the toggle ignores a click right after the list closed)
        Assert.Equal(12, vm.PickerMonths.Count);
        Assert.Equal((year - 2).ToString(), vm.PickerTitle);                               // the months of a year again
    });

    [Fact]
    public void Year_BudgetsAddUpTheMonthlyLimits() => WithApp((app, vm, cat) =>
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var last = today.Year - 1;
        app.Core.Budgets.SetLimit(cat("Spożywcze"), 100m, new DateOnly(last, 1, 1));       // valid from January of last year on
        vm.Month = new DateOnly(last, 6, 1);
        Assert.Contains(vm.BudgetBars, b => b.Path == "Spożywcze" && b.Text.Contains("100"));    // a month: 0 of 100

        vm.SetModeCommand.Execute("Year");
        var bar = vm.BudgetBars.Single(b => b.Path == "Spożywcze");
        Assert.Contains("1 200", bar.Text.Replace(" ", " "));                          // twelve months of 100
        Assert.Contains("300", bar.Text);                                                  // the 300 spent in January
        Assert.Equal("25%", bar.PercentText);
        Assert.Equal("Ok", bar.Status);
        Assert.Contains("Budżety w roku", vm.BudgetsTitle);
    });
}
