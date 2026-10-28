using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using BudzetDomowy.App.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// The expenses by category as a doughnut and as bars, both kept alive from one month to the next: each category has one series in
/// the ring and one bar, so showing another month only changes the amounts. The ring stays where it is, its colours move to the new
/// shares, and the bars grow or shrink in place; nothing is drawn again from the centre or from the bottom. Slices that are gone
/// shrink to nothing and are removed as soon as that has happened (a slice of no size left in the ring would be listed in the tooltip
/// of its neighbour); new ones are added at the end. Only the very first drawing is animated from the beginning.
/// </summary>
public sealed class MorphingBreakdown
{
    private sealed class Slice
    {
        public required string Key { get; init; }
        public required ObservableValue Value { get; init; }
        public required PieSeries<ObservableValue> Ring { get; init; }
        public required BarItem Bar { get; init; }
        public string Color { get; set; } = "";
        public bool Gone { get; set; }
    }

    private static readonly TimeSpan GhostLifetime = TimeSpan.FromMilliseconds(800);

    private readonly List<Slice> _slices = [];
    private DispatcherTimer? _cleanup;

    /// <summary>The series of the doughnut; the instance never changes, its content does.</summary>
    public ObservableCollection<ISeries> Donut { get; } = [];

    /// <summary>The bars (one per category), also never replaced.</summary>
    public ObservableCollection<BarItem> Bars { get; } = [];

    /// <summary>"1 234,00 zł (23,4%)": the amount of the slice and its share of the ring.</summary>
    private string RingTip(ChartPoint point)
    {
        var value = point.Coordinate.PrimaryValue;
        var total = _slices.Where(s => !s.Gone).Sum(s => s.Value.Value ?? 0);
        var share = total > 0 ? value / total * 100 : 0;
        return ((decimal)value).ToString("C2", CultureInfo.CurrentCulture) + " (" + share.ToString("0.#", CultureInfo.CurrentCulture) + "%)";
    }

    /// <param name="slices">Key (what makes a slice the same slice in another month), the name, the colour and the amount.</param>
    public void Update(IEnumerable<(string Key, string Name, string Color, decimal Amount)> slices, double innerRadius = 70)
    {
        var target = slices.ToList();
        var keys = target.Select(s => s.Key).ToHashSet();

        foreach (var gone in _slices.Where(s => !keys.Contains(s.Key)).ToList())
        {
            if (gone.Gone && gone.Value.Value is null or 0)
            {
                Remove(gone);
            }
            else
            {
                gone.Gone = true;
                gone.Value.Value = 0;                                          // shrinks away and is removed a moment later
                gone.Bar.Amount = 0;
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
                    ToolTipLabelFormatter = RingTip,
                };
                var bar = new BarItem { Name = name, Color = color, Amount = (double)amount };
                slice = new Slice { Key = key, Value = value, Ring = ring, Bar = bar, Color = color };
                _slices.Add(slice);
                Donut.Add(ring);
                Bars.Add(bar);
                continue;
            }

            slice.Gone = false;
            slice.Value.Value = (double)amount;
            slice.Bar.Amount = (double)amount;
            slice.Ring.Name = name;
            slice.Ring.InnerRadius = innerRadius;
            slice.Bar.Name = name;
            if (!string.Equals(slice.Color, color, StringComparison.OrdinalIgnoreCase))
            {
                slice.Color = color;
                slice.Ring.Fill = new SolidColorPaint(ChartFactory.ParseColor(color));
                slice.Bar.Color = color;
            }
        }

        if (_slices.Any(s => s.Gone)) ScheduleCleanup();
    }

    private void Remove(Slice slice)
    {
        _slices.Remove(slice);
        Donut.Remove(slice.Ring);
        Bars.Remove(slice.Bar);
    }

    /// <summary>Removes the slices that have shrunk to nothing.</summary>
    public void RemoveGone()
    {
        foreach (var s in _slices.Where(s => s.Gone).ToList()) Remove(s);
    }

    private void ScheduleCleanup()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null) return;
        if (_cleanup is null)
        {
            _cleanup = new DispatcherTimer(GhostLifetime, DispatcherPriority.Background, (_, _) =>
            {
                _cleanup?.Stop();
                RemoveGone();
            }, dispatcher);
        }
        _cleanup.Stop();                                                       // every change gives the shrinking another moment
        _cleanup.Start();
    }
}
