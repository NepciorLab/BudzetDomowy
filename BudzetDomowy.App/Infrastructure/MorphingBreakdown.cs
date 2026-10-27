using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// The expenses by category as a doughnut and as a bar chart, both kept alive from one month to the next: each category has one series
/// in each chart and they share one value, so showing another month only changes the values. The ring stays where it is, its colours
/// move to the new shares, and the bars grow or shrink in place; nothing is drawn again from the centre or from the bottom. Slices
/// that are gone shrink to nothing and are removed the next time; new ones are added at the end. Only the very first drawing is
/// animated from the beginning.
/// </summary>
public sealed class MorphingBreakdown
{
    private sealed class Slice
    {
        public required string Key { get; init; }
        public required ObservableValue Value { get; init; }
        public required PieSeries<ObservableValue> Ring { get; init; }
        public required ColumnSeries<ObservableValue> Bar { get; init; }
        public string Color { get; set; } = "";
    }

    private readonly List<Slice> _slices = [];

    /// <summary>The series of the doughnut; the instance never changes, its content does.</summary>
    public ObservableCollection<ISeries> Donut { get; } = [];

    /// <summary>The series of the bar chart (one bar per category), also never replaced.</summary>
    public ObservableCollection<ISeries> Bars { get; } = [];

    /// <param name="slices">Key (what makes a slice the same slice in another month), the name, the colour and the amount.</param>
    public void Update(IEnumerable<(string Key, string Name, string Color, decimal Amount)> slices, double innerRadius = 70)
    {
        var target = slices.ToList();
        var keys = target.Select(s => s.Key).ToHashSet();

        foreach (var gone in _slices.Where(s => !keys.Contains(s.Key)).ToList())
        {
            if (gone.Value.Value is null or 0)
            {
                _slices.Remove(gone);
                Donut.Remove(gone.Ring);
                Bars.Remove(gone.Bar);
            }
            else
            {
                gone.Value.Value = 0;                                          // shrinks away; removed with the next change
            }
        }

        foreach (var (key, name, color, amount) in target)
        {
            var slice = _slices.FirstOrDefault(s => s.Key == key);
            if (slice is null)
            {
                var value = new ObservableValue((double)amount);
                var ring = new PieSeries<ObservableValue>
                {
                    Values = new ObservableCollection<ObservableValue> { value },
                    Name = name,
                    InnerRadius = innerRadius,
                    Fill = new SolidColorPaint(ChartFactory.ParseColor(color)),
                    Stroke = null,
                    ToolTipLabelFormatter = ChartFactory.MoneyTip,
                };
                var bar = new ColumnSeries<ObservableValue>
                {
                    Values = new ObservableCollection<ObservableValue> { value },
                    Name = name,
                    Fill = new SolidColorPaint(ChartFactory.ParseColor(color)),
                    MaxBarWidth = 56,
                    YToolTipLabelFormatter = ChartFactory.MoneyTip,
                };
                slice = new Slice { Key = key, Value = value, Ring = ring, Bar = bar, Color = color };
                _slices.Add(slice);
                Donut.Add(ring);
                Bars.Add(bar);
                continue;
            }

            slice.Value.Value = (double)amount;
            slice.Ring.Name = name;
            slice.Bar.Name = name;
            if (!string.Equals(slice.Color, color, StringComparison.OrdinalIgnoreCase))
            {
                slice.Color = color;
                slice.Ring.Fill = new SolidColorPaint(ChartFactory.ParseColor(color));
                slice.Bar.Fill = new SolidColorPaint(ChartFactory.ParseColor(color));
            }
        }
    }
}
