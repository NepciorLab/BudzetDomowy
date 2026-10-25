using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using LiveChartsCore.SkiaSharpView;

namespace BudzetDomowy.UiTests;

/// <summary>The same amount for all selected transactions, and the room above the highest bar of the sum chart.</summary>
public class BulkAmountUiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() => test(app));
    }

    private static TransactionsViewModel Setup(TestApp app, int count)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < count; i++) app.Core.Transactions.Add(date, TransactionType.Expense, 10m + i, null, "n" + i, null, place: "p" + i);
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("Range");
        vm.From = DateTime.Today.AddDays(-5);
        vm.To = DateTime.Today.AddDays(1);
        vm.Reload();
        return vm;
    }

    [Fact]
    public void TheSelectedEntriesGetTheSameAmount_AndTheOthersAreLeftAlone() => WithApp(app =>
    {
        var vm = Setup(app, 5);
        var chosen = vm.Rows.Take(3).ToList();
        var ids = chosen.Select(r => r.Id).ToHashSet();
        vm.SetSelection(chosen);

        vm.ChangeAmountOfSelected(42.5m);

        var all = app.Core.Reader.GetTransactions();
        Assert.All(all.Where(t => ids.Contains(t.Id)), t => Assert.Equal(42.5m, t.AmountPln));
        Assert.Equal(2, all.Count(t => !ids.Contains(t.Id) && t.AmountPln != 42.5m));
        var toast = app.Get<ToastService>();
        Assert.Equal(("Zmieniono kwotę wpisów: 3", true), (toast.Message, toast.CanUndo));

        toast.Undo();                                                                         // the message offers the way back
        Assert.DoesNotContain(app.Core.Reader.GetTransactions(), t => t.AmountPln == 42.5m);
    });

    [Fact]
    public void AnAmountThatIsNotPositive_ChangesNothing() => WithApp(app =>
    {
        var vm = Setup(app, 3);
        vm.SetSelection(vm.Rows.ToList());
        var before = app.Core.Reader.GetTransactions().Select(t => t.AmountPln).OrderBy(a => a).ToList();
        vm.ChangeAmountOfSelected(0);
        vm.SetSelection([]);
        vm.ChangeAmountOfSelected(5);                                                         // nothing selected
        Assert.Equal(before, app.Core.Reader.GetTransactions().Select(t => t.AmountPln).OrderBy(a => a).ToList());
    });

    [Fact]
    public void ABigSelection_IsChangedInTheBackground_WithANoteOnTheBar() => WithApp(app =>
    {
        var vm = Setup(app, 8);
        var busy = BusyStatus.Instance;
        var threshold = BusyStatus.BulkThreshold;
        var executor = busy.Executor;
        var seen = new List<(bool, string)>();
        BusyStatus.BulkThreshold = 5;
        busy.Executor = work =>
        {
            seen.Add((busy.IsBusy, busy.Text));
            work();
            return Task.CompletedTask;
        };
        try
        {
            vm.SetSelection(vm.Rows.ToList());
            vm.ChangeAmountOfSelected(7m);
        }
        finally
        {
            BusyStatus.BulkThreshold = threshold;
            busy.Executor = executor;
        }

        Assert.Equal([(true, "Zmiana kwoty wpisów: 8…")], seen);
        Assert.False(busy.IsBusy);
        Assert.All(app.Core.Reader.GetTransactions(), t => Assert.Equal(7m, t.AmountPln));
    });

    [Fact]
    public void TheContextMenuAndTheFlyoutExist() => WithApp(app =>
    {
        var view = new TransactionsView { DataContext = Setup(app, 3) };
        UiHost.Layout(view, 1200, 600);
        Assert.NotNull(view.FindName("AmountFlyout"));
        Assert.NotNull(view.FindName("AmountBox"));
        var grid = Assert.IsType<DataGrid>(view.FindName("Grid"));
        var items = grid.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        Assert.Equal(["Zmień kategorię zaznaczonych…", "Zmień kwotę…"], items.Take(2));
    });

    // ----- the sum chart keeps room for the figure above its highest bar -----

    [Fact]
    public void TheAxisOfTheSumChart_LeavesRoomAboveTheHighestBar() => WithApp(app =>
    {
        var axis = Assert.Single(ChartFactory.SignedMoneyAxes([240_000m, 192_976m, 47_024m]));
        Assert.Equal(0, axis.MinLimit);
        Assert.True(axis.MaxLimit > 240_000 * 1.1);                                           // the figure above the tallest bar fits
        Assert.True(axis.MaxLimit < 240_000 * 1.3);                                           // without wasting half of the chart

        var negative = Assert.Single(ChartFactory.SignedMoneyAxes([100m, 300m, -200m]));
        Assert.True(negative.MinLimit < -200);                                                // the figure under a negative bar fits too
        Assert.True(negative.MaxLimit > 300);

        var onlyNegative = Assert.Single(ChartFactory.SignedMoneyAxes([-50m]));
        Assert.True(onlyNegative.MaxLimit > 0);
        Assert.True(onlyNegative.MinLimit < -50);

        var none = Assert.Single(ChartFactory.SignedMoneyAxes());
        Assert.Null(none.MaxLimit);
        Assert.Null(none.MinLimit);
        var zeros = Assert.Single(ChartFactory.SignedMoneyAxes([0m, 0m]));
        Assert.Null(zeros.MaxLimit);
    });

    [Fact]
    public void TheOverviewChart_UsesIt_ForWhateverIsShown() => WithApp(app =>
    {
        var date = new DateOnly(DateTime.Today.Year, 1, 15);
        app.Core.Transactions.Add(date, TransactionType.Income, 1000m, null, "p", null);
        app.Core.Transactions.Add(date, TransactionType.Expense, 400m, null, "w", null);
        var vm = app.Get<ReportsViewModel>();

        var all = Assert.IsType<Axis>(Assert.Single(vm.SumYAxes));
        Assert.True(all.MaxLimit > 1000);                                                      // the income is the highest bar

        vm.SetTrendShowCommand.Execute("Expense");
        var expenses = Assert.IsType<Axis>(Assert.Single(vm.SumYAxes));
        Assert.True(expenses.MaxLimit > 400 && expenses.MaxLimit < 1000);                      // only the expenses: scaled to them
    });
}
