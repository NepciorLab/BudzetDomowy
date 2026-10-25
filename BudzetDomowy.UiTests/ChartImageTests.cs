using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core.Domain;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;

namespace BudzetDomowy.UiTests;

public class ChartImageTests
{
    [Fact]
    public void SumChart_IsDrawnFromZero_WithRoomForTheFigures()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var date = new DateOnly(DateTime.Today.Year, 1, 15);
            app.Core.Transactions.Add(date, TransactionType.Income, 240000m, null, "p", null);
            app.Core.Transactions.Add(date, TransactionType.Expense, 192976m, null, "w", null);
            var vm = app.Get<ReportsViewModel>();
            var chart = new SKCartesianChart
            {
                Width = 360, Height = 300, Series = vm.SumSeries.ToArray(), XAxes = vm.SumXAxes.Cast<Axis>().ToArray(), YAxes = vm.SumYAxes.Cast<Axis>().ToArray(),
                Background = SkiaSharp.SKColors.DarkSlateGray,
            };
            var path = Path.Combine(UiHost.OutputDir, "sum-chart.png");
            chart.SaveImage(path);
            Assert.True(new FileInfo(path).Length > 1000);                                      // drawn: look at the picture to judge the scale

            var axis = Assert.IsType<Axis>(Assert.Single(vm.SumYAxes));
            Assert.Equal(0, axis.MinLimit);                                                     // the bars start at zero, not at a "cut" level
            Assert.True(axis.MaxLimit > 240000);
        });
    }

    [Fact]
    public void EveryTooltipShowsMoneyWithThousandsSeparated()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var money = typeof(ChartFactory).GetMethod(nameof(ChartFactory.MoneyTip))!;
            var pie = ChartFactory.Donut([("Auto", "#445566", 22914.32m)]);
            Assert.Equal(money, ((PieSeries<double>)pie[0]).ToolTipLabelFormatter!.Method);
            var bars = ChartFactory.IncomeExpenseColumns([], "P", "W");
            Assert.All(bars, s => Assert.Equal(money, ((ColumnSeries<double>)s).YToolTipLabelFormatter!.Method));
            var lines = ChartFactory.IncomeExpenseColumns([], "P", "W", lines: true);
            Assert.All(lines, s => Assert.Equal(money, ((LineSeries<double>)s).YToolTipLabelFormatter!.Method));
            Assert.All(ChartFactory.SumColumns(1, 2, "a", "b", "c"), s => Assert.Equal(money, ((ColumnSeries<double>)s).YToolTipLabelFormatter!.Method));
        });
    }
}
