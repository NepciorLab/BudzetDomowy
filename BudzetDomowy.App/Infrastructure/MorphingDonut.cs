using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// A doughnut that keeps one series per slice and only changes their values, so showing another month does not draw the ring again
/// from the centre: the ring stays where it is and the colours move to the new shares. Slices that are gone shrink to nothing and are
/// removed the next time; new ones are added at the end. Only the very first drawing is animated from the centre.
/// </summary>
public sealed class MorphingDonut
{
    private sealed class Slice
    {
        public required string Key { get; init; }
        public required ObservableValue Value { get; init; }
        public required PieSeries<ObservableValue> Series { get; init; }
        public string Color { get; set; } = "";
    }

    private readonly List<Slice> _slices = [];

    /// <summary>The series to give the chart once; the instance never changes, its content does.</summary>
    public ObservableCollection<ISeries> Series { get; } = [];

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
                Series.Remove(gone.Series);
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
                var series = new PieSeries<ObservableValue>
                {
                    Values = new ObservableCollection<ObservableValue> { value },
                    Name = name,
                    InnerRadius = innerRadius,
                    Fill = new SolidColorPaint(ChartFactory.ParseColor(color)),
                    Stroke = null,
                    ToolTipLabelFormatter = ChartFactory.MoneyTip,
                };
                slice = new Slice { Key = key, Value = value, Series = series, Color = color };
                _slices.Add(slice);
                Series.Add(series);
                continue;
            }

            slice.Value.Value = (double)amount;
            slice.Series.Name = name;
            if (!string.Equals(slice.Color, color, StringComparison.OrdinalIgnoreCase))
            {
                slice.Color = color;
                slice.Series.Fill = new SolidColorPaint(ChartFactory.ParseColor(color));
            }
        }
    }
}
