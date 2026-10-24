using BudzetDomowy.Core.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Measure;
using ModernWpf;
using SkiaSharp;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Builds LiveCharts series in the colours of the current theme.</summary>
public static class ChartFactory
{
    public static readonly string[] Palette =
        ["#1E88E5", "#43A047", "#FB8C00", "#8E24AA", "#E53935", "#00ACC1", "#FFB300", "#5C6BC0", "#EC407A", "#7CB342"];

    public static string ColorOrDefault(string color, int index) => color.Length > 0 ? color : Palette[index % Palette.Length];

    public static SKColor ParseColor(string hex) => SKColor.TryParse(hex, out var c) ? c : SKColors.Gray;

    private static bool Dark => ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;

    public static SKColor TextColor => Dark ? new SKColor(0xE6, 0xE6, 0xE6) : new SKColor(0x30, 0x30, 0x30);

    public static SolidColorPaint TextPaint() => new(TextColor);

    public static SolidColorPaint GridPaint() => new(TextColor.WithAlpha(45)) { StrokeThickness = 1 };

    public static IReadOnlyList<ISeries> Donut(IEnumerable<(string Name, string Color, decimal Amount)> slices, double innerRadius = 70) =>
        slices.Select(s => (ISeries)new PieSeries<double>
        {
            Values = [(double)s.Amount],
            Name = s.Name,
            InnerRadius = innerRadius,
            Fill = new SolidColorPaint(ParseColor(s.Color)),
            Stroke = null,
        }).ToList();

    /// <summary>Income (green) and expense (red) columns per month.</summary>
    public static IReadOnlyList<ISeries> IncomeExpenseColumns(IReadOnlyList<MonthPoint> points, string incomeName, string expenseName) =>
    [
        new ColumnSeries<double>
        {
            Name = incomeName,
            Values = points.Select(p => (double)p.Income).ToArray(),
            Fill = new SolidColorPaint(new SKColor(0x43, 0xA0, 0x47)),
            MaxBarWidth = 22,
        },
        new ColumnSeries<double>
        {
            Name = expenseName,
            Values = points.Select(p => (double)p.Expense).ToArray(),
            Fill = new SolidColorPaint(new SKColor(0xE5, 0x39, 0x35)),
            MaxBarWidth = 22,
        },
    ];

    /// <summary>One column series per year, twelve months each.</summary>
    public static IReadOnlyList<ISeries> YearColumns(IReadOnlyList<YearSeries> years) =>
        years.Select((y, i) => (ISeries)new ColumnSeries<double>
        {
            Name = y.Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
            Values = y.Months.Select(m => (double)m).ToArray(),
            Fill = new SolidColorPaint(ParseColor(Palette[i % Palette.Length])),
            MaxBarWidth = 22,
        }).ToList();

    /// <summary>One column series per month, one column per category.</summary>
    public static IReadOnlyList<ISeries> MonthColumns(IReadOnlyList<MonthSeries> months, Func<DateOnly, string> name) =>
        months.Select((m, i) => (ISeries)new ColumnSeries<double>
        {
            Name = name(m.Month),
            Values = m.Values.Select(v => (double)v).ToArray(),
            Fill = new SolidColorPaint(ParseColor(Palette[i % Palette.Length])),
            MaxBarWidth = 26,
        }).ToList();

    public static IReadOnlyList<Axis> LabelAxes(IEnumerable<string> labels, double rotation = 0) =>
    [
        new Axis
        {
            Labels = labels.ToArray(),
            LabelsPaint = TextPaint(),
            LabelsRotation = rotation,
            TextSize = 12,
            SeparatorsPaint = null,
        },
    ];

    public static IReadOnlyList<Axis> MonthAxes(IReadOnlyList<MonthPoint> points, string format) =>
    [
        new Axis
        {
            Labels = points.Select(p => p.Month.ToString(format, System.Globalization.CultureInfo.CurrentCulture)).ToArray(),
            LabelsPaint = TextPaint(),
            TextSize = 12,
            SeparatorsPaint = null,
        },
    ];

    public static IReadOnlyList<Axis> MoneyAxes() =>
    [
        new Axis
        {
            LabelsPaint = TextPaint(),
            TextSize = 12,
            SeparatorsPaint = GridPaint(),
            MinLimit = 0,
            Labeler = v => v.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        },
    ];
}
