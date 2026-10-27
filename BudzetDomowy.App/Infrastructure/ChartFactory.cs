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

    /// <summary>
    /// Slices that have the same colour (subcategories usually take the one of their main category) get shades of it, from a little
    /// darker to much lighter, so they can be told apart in the ring, the bars and the legend; colours that are alone stay as they are.
    /// </summary>
    public static IReadOnlyList<string> Distinguish(IReadOnlyList<string> colors)
    {
        var result = colors.ToArray();
        var groups = colors.Select((c, i) => (Color: c, Index: i)).Where(x => x.Color.Length > 0)
                           .GroupBy(x => x.Color, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            var members = group.ToList();
            var baseColor = ParseColor(group.Key);
            for (var j = 0; j < members.Count; j++)
            {
                var factor = -0.25 + 0.8 * j / (members.Count - 1);
                result[members[j].Index] = Shade(baseColor, factor);
            }
        }
        return result;
    }

    /// <summary>The colour made lighter (factor above 0, towards white) or darker (below 0, towards black).</summary>
    public static string Shade(SKColor color, double factor)
    {
        byte Mix(byte c) => (byte)Math.Clamp(Math.Round(factor >= 0 ? c + (255 - c) * factor : c * (1 + factor)), 0, 255);
        return $"#{Mix(color.Red):X2}{Mix(color.Green):X2}{Mix(color.Blue):X2}";
    }

    public static SKColor ParseColor(string hex) => SKColor.TryParse(hex, out var c) ? c : SKColors.Gray;

    private static bool Dark => ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;

    public static SKColor TextColor => Dark ? new SKColor(0xE6, 0xE6, 0xE6) : new SKColor(0x30, 0x30, 0x30);

    public static SolidColorPaint TextPaint() => new(TextColor);

    public static SolidColorPaint GridPaint() => new(TextColor.WithAlpha(45)) { StrokeThickness = 1 };

    /// <summary>The figure in a tooltip: a sum of money with thin spaces between the thousands and the currency ("22 914,32 zł").</summary>
    public static string MoneyTip(LiveChartsCore.Kernel.ChartPoint point) =>
        ((decimal)point.Coordinate.PrimaryValue).ToString("C2", System.Globalization.CultureInfo.CurrentCulture);

    public static IReadOnlyList<ISeries> Donut(IEnumerable<(string Name, string Color, decimal Amount)> slices, double innerRadius = 70) =>
        slices.Select(s => (ISeries)new PieSeries<double>
        {
            Values = [(double)s.Amount],
            Name = s.Name,
            InnerRadius = innerRadius,
            Fill = new SolidColorPaint(ParseColor(s.Color)),
            Stroke = null,
            ToolTipLabelFormatter = MoneyTip,
        }).ToList();

    /// <summary>Income (green) and expense (red) columns per month.</summary>
    public static IReadOnlyList<ISeries> IncomeExpenseColumns(IReadOnlyList<MonthPoint> points, string incomeName, string expenseName, bool lines = false,
        bool showIncome = true, bool showExpense = true)
    {
        var series = new List<ISeries>();
        if (showIncome) series.Add(Series(lines, incomeName, points.Select(p => p.Income), new SKColor(0x43, 0xA0, 0x47)));
        if (showExpense) series.Add(Series(lines, expenseName, points.Select(p => p.Expense), new SKColor(0xE5, 0x39, 0x35)));
        return series;
    }

    /// <summary>A line through the points, in the given colour (used instead of columns when the user prefers lines).</summary>
    private static ISeries Line(string name, IEnumerable<decimal> values, SKColor color) => new LineSeries<double>
    {
        Name = name,
        Values = values.Select(v => (double)v).ToArray(),
        Fill = null,
        LineSmoothness = 0,
        Stroke = new SolidColorPaint(color) { StrokeThickness = 3 },
        GeometrySize = 9,
        GeometryFill = new SolidColorPaint(color),
        GeometryStroke = new SolidColorPaint(color) { StrokeThickness = 3 },
        YToolTipLabelFormatter = MoneyTip,
    };

    private static ISeries Bars(string name, IEnumerable<decimal> values, SKColor color, double maxWidth) => new ColumnSeries<double>
    {
        Name = name,
        Values = values.Select(v => (double)v).ToArray(),
        Fill = new SolidColorPaint(color),
        MaxBarWidth = maxWidth,
        YToolTipLabelFormatter = MoneyTip,
    };

    private static ISeries Series(bool lines, string name, IEnumerable<decimal> values, SKColor color, double maxWidth = 22) =>
        lines ? Line(name, values, color) : Bars(name, values, color, maxWidth);

    /// <summary>One series per year, twelve months each (columns or lines).</summary>
    public static IReadOnlyList<ISeries> YearColumns(IReadOnlyList<YearSeries> years, bool lines = false) =>
        years.Select((y, i) => Series(lines, y.Year.ToString(System.Globalization.CultureInfo.CurrentCulture), y.Months,
            ParseColor(Palette[i % Palette.Length]))).ToList();

    /// <summary>One line per year: the average of the months so far (see ComparisonReports.CumulativeAverages); future months stay empty.</summary>
    public static IReadOnlyList<ISeries> YearAverages(IReadOnlyList<YearSeries> years, DateOnly today) =>
        years.Select((y, i) =>
        {
            var color = ParseColor(Palette[i % Palette.Length]);
            return (ISeries)new LineSeries<double?>
            {
                Name = y.Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
                Values = ComparisonReports.CumulativeAverages(y, today).Select(v => (double?)v).ToArray(),
                Fill = null,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(color) { StrokeThickness = 3 },
                GeometrySize = 9,
                GeometryFill = new SolidColorPaint(color),
                GeometryStroke = new SolidColorPaint(color) { StrokeThickness = 3 },
                YToolTipLabelFormatter = MoneyTip,
            };
        }).ToList();

    /// <summary>One series per month, one point or column per category.</summary>
    public static IReadOnlyList<ISeries> MonthColumns(IReadOnlyList<MonthSeries> months, Func<DateOnly, string> name, bool lines = false) =>
        months.Select((m, i) => Series(lines, name(m.Month), m.Values, ParseColor(Palette[i % Palette.Length]), 26)).ToList();

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

    /// <summary>A line per category over the same points (markers only while the lines are short enough for them to be readable).</summary>
    public static IReadOnlyList<ISeries> CategoryLines(IReadOnlyList<(string Name, string Color, IReadOnlyList<decimal> Values)> lines,
        Func<int, string>? periodOf = null)
    {
        var points = lines.Count == 0 ? 0 : lines.Max(l => l.Values.Count);
        var marker = points <= 48 ? 8 : 0;

        // the tooltip has a heading made once from the horizontal label of the point: it tells the period, the rows below tell the sums
        Func<LiveChartsCore.Kernel.ChartPoint, string>? heading = periodOf is null
            ? null
            : point => periodOf((int)Math.Round(point.Coordinate.SecondaryValue));
        return lines.Select(l =>
        {
            var color = ParseColor(l.Color);
            return (ISeries)new LineSeries<double>
            {
                Name = l.Name,
                Values = l.Values.Select(v => (double)v).ToArray(),
                Fill = null,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(color) { StrokeThickness = 2.5f },
                GeometrySize = marker,
                GeometryFill = new SolidColorPaint(color),
                GeometryStroke = new SolidColorPaint(color) { StrokeThickness = 2 },
                YToolTipLabelFormatter = MoneyTip,
                XToolTipLabelFormatter = heading ?? (_ => LiveChartsCore.LiveCharts.IgnoreToolTipLabel),
            };
        }).ToList();
    }

    /// <summary>
    /// An axis that writes exactly the given labels (empty ones stay empty). The step is forced to one: left alone, LiveCharts writes only
    /// every n-th label of a long axis and skips the ones that were meant to be shown.
    /// </summary>
    public static IReadOnlyList<Axis> SparseAxes(IReadOnlyList<string> labels) =>
    [
        new Axis
        {
            Labels = labels.ToArray(),
            LabelsPaint = TextPaint(),
            TextSize = 12,
            SeparatorsPaint = null,
            MinStep = 1,
            ForceStepToMin = true,
        },
    ];

    /// <summary>The axis of a time line: one position per point, but only about a dozen labels written (the others are left empty so they never overlap).</summary>
    public static IReadOnlyList<Axis> BucketAxes(IReadOnlyList<string> labels)
    {
        var step = Math.Max(1, (int)Math.Ceiling(labels.Count / 12.0));
        return
        [
            new Axis
            {
                Labels = labels.Select((text, i) => i % step == 0 ? text : "").ToArray(),
                LabelsPaint = TextPaint(),
                TextSize = 12,
                SeparatorsPaint = null,
            },
        ];
    }

    /// <summary>One column each for the income, the expenses and the balance of everything shown (the balance may be negative).</summary>
    public static IReadOnlyList<ISeries> SumColumns(decimal income, decimal expense, string incomeName, string expenseName, string balanceName,
        bool showIncome = true, bool showExpense = true)
    {
        ISeries Bar(string name, decimal value, SKColor color) => new ColumnSeries<double>
        {
            Name = name,
            Values = [(double)value],
            Fill = new SolidColorPaint(color),
            MaxBarWidth = 60,
            YToolTipLabelFormatter = MoneyTip,
            DataLabelsPaint = TextPaint(),
            DataLabelsSize = 12,
            DataLabelsPosition = DataLabelsPosition.End,
            DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        };
        var bars = new List<ISeries>();
        if (showIncome) bars.Add(Bar(incomeName, income, new SKColor(0x43, 0xA0, 0x47)));
        if (showExpense) bars.Add(Bar(expenseName, expense, new SKColor(0xE5, 0x39, 0x35)));
        if (showIncome && showExpense) bars.Add(Bar(balanceName, income - expense, new SKColor(0xFB, 0x8C, 0x00)));      // the balance needs both
        return bars;
    }

    /// <summary>
    /// Money axis that may go below zero. When the values the chart will show are given, the axis leaves room above the highest bar
    /// (and below the lowest) for the figure written at its end: otherwise the tallest bar reaches the top and its label is cut off.
    /// </summary>
    public static IReadOnlyList<Axis> SignedMoneyAxes(IEnumerable<decimal>? values = null)
    {
        var axis = new Axis
        {
            LabelsPaint = TextPaint(),
            TextSize = 12,
            SeparatorsPaint = GridPaint(),
            Labeler = v => v.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        };

        var list = (values ?? []).Select(v => (double)v).ToList();
        if (list.Count > 0)
        {
            var high = Math.Max(0, list.Max());
            var low = Math.Min(0, list.Min());
            var room = (high - low) * LabelRoom;
            if (room > 0)
            {
                axis.MaxLimit = high > 0 ? high + room : 0;
                axis.MinLimit = low < 0 ? low - room : 0;
                if (high == 0) axis.MaxLimit = room;                         // only negative bars: the zero line still has space above it
            }
        }
        return [axis];
    }

    /// <summary>Share of the value range kept free above the highest bar for its figure.</summary>
    private const double LabelRoom = 0.16;

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
