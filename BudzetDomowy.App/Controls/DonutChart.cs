using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// The ring of the expenses by category. It takes <see cref="ChartItem"/>s (the dashboard: the same objects as the bars, so another month
/// only changes the amounts and the slices move in place) or LiveCharts pie series (the reports: every list is drawn anew). The first
/// drawing sweeps round from the top; later changes move the borders of the slices. Pointing at a slice shows its colour, amount and
/// share at the outer edge of the slice.
/// </summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<ChartItem>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, (d, e) => ((DonutChart)d).OnItemsReplaced(e.OldValue as IEnumerable<ChartItem>, e.NewValue as IEnumerable<ChartItem>)));

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ISeries>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, (d, e) => ((DonutChart)d).OnSeriesReplaced(e.NewValue as IEnumerable<ISeries>)));

    /// <summary>Thickness of the ring in pixels.</summary>
    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender), v => v is double d && d >= 2);

    public IEnumerable<ChartItem>? Items { get => (IEnumerable<ChartItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public IEnumerable<ISeries>? Series { get => (IEnumerable<ISeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(450);

    private sealed class Slice
    {
        public required ChartItem Item { get; init; }
        public double From, To, Shown;
    }

    private List<Slice> _slices = [];
    private double _denFrom, _denTo, _den;
    private DateTime _started;
    private bool _animating;
    private int _hover = -1;
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChartTip _tip;

    public DonutChart()
    {
        SnapsToDevicePixels = false;
        _tip = new ChartTip(this);
        Unloaded += (_, _) => { StopAnimation(); _tip.Detach(); };
    }

    // ----- the data -----

    private void OnItemsReplaced(IEnumerable<ChartItem>? oldItems, IEnumerable<ChartItem>? newItems)
    {
        if (oldItems is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnCollectionChanged;
        foreach (var s in _slices) s.Item.PropertyChanged -= OnItemChanged;
        _slices = [];
        _den = 0;
        if (newItems is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnCollectionChanged;
        Retarget(newItems?.ToList() ?? []);
    }

    /// <summary>The pie series of the reports become items; every new list is a new ring.</summary>
    private void OnSeriesReplaced(IEnumerable<ISeries>? series)
    {
        if (Items is not null) return;                                              // the items are the source
        foreach (var s in _slices) s.Item.PropertyChanged -= OnItemChanged;
        _slices = [];
        _den = 0;
        Retarget((series ?? []).Select(ToItem).Where(i => i is not null).Cast<ChartItem>().ToList());
    }

    private static ChartItem? ToItem(ISeries series)
    {
        var (value, fill) = series switch
        {
            PieSeries<double> p => (p.Values?.FirstOrDefault() ?? 0, p.Fill),
            PieSeries<LiveChartsCore.Defaults.ObservableValue> p => (p.Values?.FirstOrDefault()?.Value ?? 0, p.Fill),
            _ => (double.NaN, null),
        };
        if (double.IsNaN(value)) return null;
        var color = (fill as SolidColorPaint)?.Color ?? SkiaSharp.SKColors.Gray;
        return new ChartItem { Name = series.Name ?? "", Color = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}", Amount = value };
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Retarget(Items?.ToList() ?? []);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Retarget(Items?.ToList() ?? _slices.Select(s => s.Item).ToList());

    private void Retarget(List<ChartItem> items)
    {
        var known = _slices.ToDictionary(s => s.Item);
        foreach (var s in _slices) s.Item.PropertyChanged -= OnItemChanged;

        var slices = new List<Slice>(items.Count);
        foreach (var item in items)
        {
            if (!known.TryGetValue(item, out var slice)) slice = new Slice { Item = item };          // new: grows from nothing
            slice.From = slice.Shown;
            slice.To = Math.Max(0, item.Amount);
            slices.Add(slice);
            item.PropertyChanged += OnItemChanged;
        }
        _slices = slices;

        var shownSum = slices.Sum(s => s.From);
        _denTo = slices.Sum(s => s.To);
        _denFrom = shownSum > 0 ? shownSum : _denTo;                                  // the first time the ring fills up round the circle, later it stays whole
        if (_den <= 0) _den = _denFrom;

        _hover = -1;
        _tip.Hide();
        _started = DateTime.UtcNow;
        StartAnimation();
        InvalidateVisual();
    }

    /// <summary>Finishes the movement at once (renders and tests do not wait for frames).</summary>
    public void CompleteAnimation()
    {
        StopAnimation();
        foreach (var s in _slices) s.Shown = s.To;
        _den = _denTo;
        InvalidateVisual();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = Math.Min(1, (DateTime.UtcNow - _started).TotalMilliseconds / Duration.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - t, 3);
        foreach (var s in _slices) s.Shown = s.From + (s.To - s.From) * eased;
        _den = _denFrom + (_denTo - _denFrom) * eased;
        InvalidateVisual();
        if (t >= 1) StopAnimation();
    }

    // ----- geometry -----

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 200 : available.Width, double.IsInfinity(available.Height) ? 200 : available.Height);

    private Point Centre => new(ActualWidth / 2, ActualHeight / 2);

    private double Outer => Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 4);

    private double Inner => Math.Max(2, Outer - RingThickness);

    /// <summary>Start and end of every slice in degrees clockwise from the top.</summary>
    private IEnumerable<(Slice Slice, double Start, double End)> Angles()
    {
        if (_den <= 0) yield break;
        var start = 0.0;
        foreach (var s in _slices)
        {
            var sweep = s.Shown / _den * 360;
            yield return (s, start, start + sweep);
            start += sweep;
        }
    }

    private static Point OnCircle(Point centre, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Sin(a), centre.Y - radius * Math.Cos(a));
    }

    private Geometry Segment(double start, double end, double grow = 0)
    {
        var c = Centre;
        var outer = Outer + grow;
        var inner = Inner;
        if (end - start >= 359.9)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, inner, inner));
            ring.Freeze();
            return ring;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var large = end - start > 180;
            g.BeginFigure(OnCircle(c, outer, start), true, true);
            g.ArcTo(OnCircle(c, outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            g.LineTo(OnCircle(c, inner, end), true, false);
            g.ArcTo(OnCircle(c, inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Brush BrushOf(string hex)
    {
        if (_brushes.TryGetValue(hex, out var cached)) return cached;
        Brush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (Exception e) when (e is FormatException or NotSupportedException) { brush = Brushes.Gray; }
        brush.Freeze();
        return _brushes[hex] = brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var index = 0;
        foreach (var (slice, start, end) in Angles())
        {
            if (end - start > 0.05)
            {
                dc.DrawGeometry(BrushOf(slice.Item.Color), null, Segment(start, end, index == _hover ? 3 : 0));
                if (index == _hover)
                {
                    dc.PushOpacity(0.2);
                    dc.DrawGeometry(Brushes.White, null, Segment(start, end, 3));
                    dc.Pop();
                }
            }
            index++;
        }
    }

    // ----- pointer -----

    /// <summary>The slice under the point (in the coordinates of the chart), null outside of the ring.</summary>
    public ChartItem? ItemAt(Point p) => SliceAt(p) is { } found ? found.Slice.Item : null;

    private (Slice Slice, double Start, double End, int Index)? SliceAt(Point p)
    {
        var c = Centre;
        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < Inner || distance > Outer + 3) return null;
        var degrees = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (degrees < 0) degrees += 360;
        var index = 0;
        foreach (var (slice, start, end) in Angles())
        {
            if (end - start > 0.05 && degrees >= start && degrees < end) return (slice, start, end, index);
            index++;
        }
        return null;
    }

    /// <summary>"1 234,00 zł (23,4%)".</summary>
    public string ValueOf(ChartItem item)
    {
        var total = _slices.Sum(s => s.To);
        var share = total > 0 ? item.Amount / total * 100 : 0;
        return ((decimal)item.Amount).ToString("C2", CultureInfo.CurrentCulture) + " (" + share.ToString("0.#", CultureInfo.CurrentCulture) + "%)";
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var found = SliceAt(e.GetPosition(this));
        var index = found?.Index ?? -1;
        if (index == _hover) return;
        _hover = index;
        if (found is not { } f) _tip.Hide();
        else
        {
            var anchor = OnCircle(Centre, Outer, (f.Start + f.End) / 2);
            _tip.Show(null, [new TipRow(BrushOf(f.Slice.Item.Color), f.Slice.Item.Name, ValueOf(f.Slice.Item))], anchor, preferBelow: anchor.Y > Centre.Y);
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        _tip.Hide();
        InvalidateVisual();
    }
}
