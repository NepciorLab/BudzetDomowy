using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>Transactions: the filter by amount (from, to).</summary>
public class AmountFilterTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() => test(app));
    }

    private static TransactionsViewModel Setup(TestApp app, params decimal[] amounts)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < amounts.Length; i++)
            app.Core.Transactions.Add(date, i % 3 == 2 ? TransactionType.Income : TransactionType.Expense, amounts[i], null, "n" + i, null, place: "p" + i);
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("Range");
        vm.From = DateTime.Today.AddDays(-5);
        vm.To = DateTime.Today.AddDays(1);
        vm.Reload();
        return vm;
    }

    private static List<decimal> Shown(TransactionsViewModel vm) => vm.Rows.Select(r => r.Model.AmountPln).OrderBy(a => a).ToList();

    [Fact]
    public void OnlyEntriesWithinTheRangeAreShown_BothEndsIncluded() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m, 50.01m, 100m, 200.50m, 200.51m, 999m);
        Assert.Equal(8, vm.Rows.Count);

        vm.AmountFrom = "50";
        vm.AmountTo = "200,50";

        Assert.Equal([50m, 50.01m, 100m, 200.50m], Shown(vm));
        Assert.True(vm.HasActiveFilters);
        Assert.StartsWith("Wszystkie pasujące: 4", vm.AllLabel);                              // the totals follow the filter
    });

    [Fact]
    public void OneSideIsEnough_AndADotWorksAsADecimalMark() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m, 100m, 999m);

        vm.AmountFrom = "100";
        Assert.Equal([100m, 999m], Shown(vm));

        vm.AmountFrom = "";
        vm.AmountTo = "20.00";
        Assert.Equal([5m, 20m], Shown(vm));

        vm.AmountTo = "  ";
        Assert.Equal(5, vm.Rows.Count);
        Assert.False(vm.HasActiveFilters);
    });

    [Fact]
    public void ARangeGivenTheOtherWayRound_MeansTheSame() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m, 100m, 999m);
        vm.AmountFrom = "100";
        vm.AmountTo = "20";
        Assert.Equal([20m, 50m, 100m], Shown(vm));
    });

    [Fact]
    public void WhatIsNotANumber_IsMarkedAndDoesNotNarrowTheTable() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m);

        vm.AmountFrom = "abc";
        Assert.True(vm.AmountFromInvalid);
        Assert.False(vm.AmountToInvalid);
        Assert.Equal(3, vm.Rows.Count);

        vm.AmountFrom = "-5";                                                                // an amount is never negative here
        Assert.True(vm.AmountFromInvalid);

        vm.AmountFrom = "20";
        vm.AmountTo = "x";
        Assert.False(vm.AmountFromInvalid);
        Assert.True(vm.AmountToInvalid);
        Assert.Equal([20m, 50m], Shown(vm));                                                 // the good side still counts
    });

    [Fact]
    public void ItCombinesWithTheOtherFilters_AndClearFiltersResetsIt() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m, 60m, 70m, 80m);                                    // every third one (50 and 80) is an income
        vm.AmountFrom = "20";
        vm.AmountTo = "70";
        Assert.Equal([20m, 50m, 60m, 70m], Shown(vm));

        vm.TypeFilter = vm.TypeFilters.First(f => f.Type == TransactionType.Expense);
        Assert.Equal([20m, 60m, 70m], Shown(vm));

        Assert.True(vm.ClearFiltersIfAny());                                                 // Esc clears the range as well
        Assert.Equal(("", ""), (vm.AmountFrom, vm.AmountTo));
        Assert.Equal(6, vm.Rows.Count);
        Assert.False(vm.HasActiveFilters);
    });

    [Fact]
    public void ForeignEntriesAreJudgedByTheirAmountInZloty_TheSameAsTheTotals() => WithApp(app =>
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        app.Core.Transactions.Add(date, TransactionType.Expense, 100m, null, "w euro", null, place: "x", currency: "EUR", rateToPln: 4.3m);
        app.Core.Transactions.Add(date, TransactionType.Expense, 100m, null, "w zlotych", null, place: "y");
        var vm = Setup(app);

        vm.AmountFrom = "400";
        Assert.Equal([430m], Shown(vm));                                                     // 100 EUR is 430 zł
        vm.AmountFrom = "";
        vm.AmountTo = "100";
        Assert.Equal([100m], Shown(vm));
    });

    [Fact]
    public void TheViewHasTwoBoxesForIt_NextToTheOtherFilters() => WithApp(app =>
    {
        var vm = Setup(app, 5m, 20m, 50m);
        var view = new TransactionsView { DataContext = vm };
        UiHost.Layout(view, 1200, 600);

        var from = Assert.IsType<TextBox>(view.FindName("AmountFromBox"));
        var to = Assert.IsType<TextBox>(view.FindName("AmountToBox"));
        Assert.Equal(from.Parent, to.Parent);                                                // the same row of filters as the search box
        var texts = UiHost.Texts(view);
        from.Text = "10";
        to.Text = "30";
        Assert.Equal(("10", "30"), (vm.AmountFrom, vm.AmountTo));                            // typing reaches the view model
        Assert.Equal([20m], Shown(vm));
        UiHost.Render(view, 1200, 600, "transactions-amount-filter");
        Assert.NotNull(texts);
    });
}
