using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Controls;

/// <summary>One bar of <see cref="BarBreakdownChart"/> or slice of <see cref="DonutChart"/>: a category, its colour (#RRGGBB) and the amount; a change of the amount moves the bar.</summary>
public sealed class ChartItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _color = "";
    private double _amount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }

    public string Color { get => _color; set => Set(ref _color, value, nameof(Color)); }

    public double Amount { get => _amount; set => Set(ref _amount, value, nameof(Amount)); }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Columns of the expenses by category: the chart is put at the left edge; the horizontal lines of the grid reach half a bar beyond the
/// first and the last bar and end there instead of running over the whole card. The names stand under the bars, turned upright and
/// read from the bottom up. The height never changes (the names have a place of their own), so nothing jumps when another month is
/// shown. Another amount moves the bar (and the scale) smoothly; a bar that comes new grows from the bottom. Pointing at a bar shows
/// a tip with its colour, amount and share above it.
/// </summary>
public sealed class BarBreakdownChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<ChartItem>), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(null, (d, e) => ((BarBreakdownChart)d).OnItemsReplaced(e.OldValue as IEnumerable<ChartItem>, e.NewValue as IEnumerable<ChartItem>)));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender), v => v is double d && d >= 2);

    /// <summary>Space between two bars in percent of the width of a bar.</summary>
    public static readonly DependencyProperty GapPercentProperty = DependencyProperty.Register(
        nameof(GapPercent), typeof(double), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(25.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender), v => v is double d && d >= 0);

    /// <summary>A bar that is much higher than all the others is cut short and marked with a break (its real amount is written above it), so the others do not shrink to lines.</summary>
    public static readonly DependencyProperty BreakLargestProperty = DependencyProperty.Register(
        nameof(BreakLargest), typeof(bool), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BarBreakdownChart)d).Retarget()));

    /// <summary>The colour of the card behind the chart: the break is a band of it across the cut bar.</summary>
    public static readonly DependencyProperty BreakBrushProperty = DependencyProperty.Register(
        nameof(BreakBrush), typeof(Brush), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(BarBreakdownChart), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public IEnumerable<ChartItem>? Items { get => (IEnumerable<ChartItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }

    public double GapPercent { get => (double)GetValue(GapPercentProperty); set => SetValue(GapPercentProperty, value); }

    public bool BreakLargest { get => (bool)GetValue(BreakLargestProperty); set => SetValue(BreakLargestProperty, value); }

    public Brush BreakBrush { get => (Brush)GetValue(BreakBrushProperty); set => SetValue(BreakBrushProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public const double PlotHeight = 150;
    public const double TopPadding = 24;                                  // room for the figure above a bar that was cut short
    /// <summary>The largest bar is cut when it is more than this many times higher than the next one.</summary>
    public const double BreakRatio = 3;
    /// <summary>The scale then reaches this many times the next bar.</summary>
    private const double BreakRoom = 1.25;
    public const double LabelGap = 6;
    public const double LabelHeight = 110;
    private const double GridLines = 4;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(450);

    private sealed class Bar
    {
        public required ChartItem Item { get; init; }
        public double From, To, Shown;
    }

    private List<Bar> _bars = [];
    private double _fromMax, _toMax, _shownMax;
    private DateTime _started;
    private bool _animating;
    private double _axisWidth;
    private int _hover = -1;
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);

    private readonly ChartTip _tip;

    public BarBreakdownChart()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        SnapsToDevicePixels = true;
        _tip = new ChartTip(this);
        Unloaded += (_, _) => { StopAnimation(); _tip.Detach(); };
    }

    /// <summary>Space between two bars.</summary>
    public double Gap => BarWidth * GapPercent / 100;

    /// <summary>The grid reaches this far beyond the first and the last bar: half a bar.</summary>
    public double Overhang => BarWidth / 2;

    /// <summary>Distance from one bar to the next.</summary>
    public double SlotWidth => BarWidth + Gap;

    /// <summary>The horizontal lines of the grid run from here to <see cref="PlotRight"/>.</summary>
    public double PlotLeft => _axisWidth;

    public double PlotRight => _bars.Count == 0 ? _axisWidth : _axisWidth + 2 * Overhang + _bars.Count * BarWidth + (_bars.Count - 1) * Gap;

    private double BarLeft(int index) => PlotLeft + Overhang + index * SlotWidth;

    // ----- the items -----

    private void OnItemsReplaced(IEnumerable<ChartItem>? oldItems, IEnumerable<ChartItem>? newItems)
    {
        if (oldItems is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _bars) item.Item.PropertyChanged -= OnItemChanged;
        _bars = [];
        _shownMax = 0;
        if (newItems is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnCollectionChanged;
        Retarget();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Retarget();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Retarget();

    private void Retarget()
    {
        var items = Items?.ToList() ?? [];
        var known = _bars.ToDictionary(b => b.Item);
        foreach (var b in _bars) b.Item.PropertyChanged -= OnItemChanged;

        var bars = new List<Bar>(items.Count);
        foreach (var item in items)
        {
            if (!known.TryGetValue(item, out var bar)) bar = new Bar { Item = item };     // new: grows from the bottom
            bar.From = bar.Shown;
            bar.To = Math.Max(0, item.Amount);
            bars.Add(bar);
            item.PropertyChanged += OnItemChanged;
        }
        _bars = bars;

        _broken = BrokenBar(bars);
        _fromMax = _shownMax;
        _toMax = NiceMax(bars.Count == 0 ? 0 : _broken is { } cut ? bars.Where(b => b != cut).Max(b => b.To) * BreakRoom : bars.Max(b => b.To));
        if (_shownMax <= 0) _shownMax = _fromMax = _toMax;                                 // the scale is there from the start, only the bars grow

        HideTip();
        _started = DateTime.UtcNow;
        StartAnimation();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Finishes the movement at once (the next frame would do it anyway; tests and renders do not wait for frames).</summary>
    public void CompleteAnimation()
    {
        StopAnimation();
        foreach (var b in _bars) b.Shown = b.To;
        _shownMax = _toMax;
        InvalidateVisual();
    }

    // ----- movement -----

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
        foreach (var b in _bars) b.Shown = b.From + (b.To - b.From) * eased;
        _shownMax = _fromMax + (_toMax - _fromMax) * eased;
        InvalidateVisual();
        if (t >= 1) StopAnimation();
    }

    /// <summary>The top of the scale: the amount rounded up to 1, 2, 2.5 or 5 (times a power of ten) per division of the grid.</summary>
    public static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        var raw = max / GridLines;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1d, 2d, 2.5d, 5d, 10d }.Select(f => f * magnitude).First(s => s >= raw - 1e-9);
        return step * GridLines;
    }

    // ----- layout -----

    private FormattedText Text(string text, Brush brush, double maxWidth = double.PositiveInfinity)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this));
        var size = TextElement.GetFontSize(this);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size * 0.9, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private static string Tick(double value) => (Math.Round(value) + 0.0).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The height is the same for every month and every list of categories; the width is what the bars need (a scroll viewer around the chart takes care of too many).</summary>
    protected override Size MeasureOverride(Size available)
    {
        var brush = Foreground;
        _axisWidth = Enumerable.Range(0, (int)GridLines + 1).Max(k => Text(Tick(_toMax * k / GridLines), brush).WidthIncludingTrailingWhitespace) + 10;
        return new Size(PlotRight, TopPadding + PlotHeight + LabelGap + LabelHeight);
    }

    // ----- drawing -----

    private Brush BrushOf(string hex)
    {
        if (_brushes.TryGetValue(hex, out var cached)) return cached;
        Brush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (Exception e) when (e is FormatException or NotSupportedException) { brush = Brushes.Gray; }
        brush.Freeze();
        return _brushes[hex] = brush;
    }

    private Bar? _broken;

    /// <summary>The top of the scale the bars are drawn against (after the movement is over).</summary>
    public double ScaleMax => _toMax;

    /// <summary>The bar that is cut short, if there is one.</summary>
    public ChartItem? BrokenItem => _broken?.Item;

    private Bar? BrokenBar(List<Bar> bars)
    {
        if (!BreakLargest || bars.Count < 2) return null;
        var ordered = bars.OrderByDescending(b => b.To).ToList();
        return ordered[1].To > 0 && ordered[0].To > BreakRatio * ordered[1].To ? ordered[0] : null;
    }

    private double HeightOf(Bar bar) => _shownMax > 0 ? Math.Min(PlotHeight, PlotHeight * bar.Shown / _shownMax) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var n = _bars.Count;
        if (n == 0) return;
        var fg = Foreground;
        var bottom = TopPadding + PlotHeight;

        // the grid: only as wide as the bars with half a bar on both sides
        var line = new Pen(fg, 1);
        dc.PushOpacity(0.18);
        for (var k = 0; k <= GridLines; k++)
        {
            var y = Math.Floor(bottom - PlotHeight * k / GridLines) + 0.5;
            dc.DrawLine(line, new Point(PlotLeft, y), new Point(PlotRight, y));
        }
        dc.Pop();

        for (var k = 0; k <= GridLines; k++)
        {
            var text = Text(Tick(_shownMax * k / GridLines), fg);
            dc.PushOpacity(0.7);
            dc.DrawText(text, new Point(PlotLeft - 6 - text.WidthIncludingTrailingWhitespace, bottom - PlotHeight * k / GridLines - text.Height / 2));
            dc.Pop();
        }

        for (var i = 0; i < n; i++)
        {
            var bar = _bars[i];
            var left = BarLeft(i);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(left - Gap / 2, TopPadding, SlotWidth, PlotHeight + LabelGap + LabelHeight));   // the whole column reacts to the pointer

            var h = HeightOf(bar);
            if (h > 0)
            {
                var rect = new Rect(left, bottom - h, BarWidth, h);
                dc.DrawRectangle(BrushOf(bar.Item.Color), null, rect);
                if (bar == _broken && h >= PlotHeight - 0.5) DrawBreak(dc, rect, fg, bar);
                if (i == _hover)
                {
                    dc.PushOpacity(0.22);
                    dc.DrawRectangle(Brushes.White, null, rect);
                    dc.Pop();
                }
            }

            // the name, upright under the bar and read from the bottom up; its end is at the axis
            var name = Text(bar.Item.Name, fg, LabelHeight);
            var matrix = Matrix.Identity;
            matrix.Rotate(-90);
            matrix.Translate(left + BarWidth / 2 - name.Height / 2, bottom + LabelGap + name.Width);
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawText(name, new Point(0, 0));
            dc.Pop();
        }
    }

    /// <summary>The cut bar: a slanted band of the background across its upper part and the real amount above it.</summary>
    private void DrawBreak(DrawingContext dc, Rect rect, Brush fg, Bar bar)
    {
        var y = rect.Top + 16;
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            g.BeginFigure(new Point(rect.Left - 1, y + 6), true, true);
            g.LineTo(new Point(rect.Right + 1, y - 2), true, false);
            g.LineTo(new Point(rect.Right + 1, y + 3), true, false);
            g.LineTo(new Point(rect.Left - 1, y + 11), true, false);
        }
        band.Freeze();
        dc.DrawGeometry(BreakBrush, null, band);

        var label = Text(Math.Round(bar.To).ToString("N0", CultureInfo.CurrentCulture), fg);
        dc.DrawText(label, new Point(rect.Left + rect.Width / 2 - label.WidthIncludingTrailingWhitespace / 2, rect.Top - label.Height - 2));
    }

    // ----- pointer -----

    /// <summary>The bar at the point: its column, from the top of the bar down to the end of the name.</summary>
    public ChartItem? ItemAt(Point p)
    {
        var n = _bars.Count;
        var first = PlotLeft + Overhang - Gap / 2;
        var last = BarLeft(n - 1) + BarWidth + Gap / 2;
        if (n == 0 || p.X < first || p.X >= last) return null;
        var i = Math.Clamp((int)((p.X - first) / SlotWidth), 0, n - 1);
        var bottom = TopPadding + PlotHeight;
        var top = bottom - Math.Max(HeightOf(_bars[i]), 0);
        return p.Y >= top && p.Y <= bottom + LabelGap + LabelHeight ? _bars[i].Item : null;
    }

    /// <summary>The share of the bar in all of them, in percent.</summary>
    private double ShareOf(ChartItem item)
    {
        var total = _bars.Sum(b => b.To);
        return total > 0 ? item.Amount / total * 100 : 0;
    }

    private static string Money(double amount) => ((decimal)amount).ToString("C2", CultureInfo.CurrentCulture);

    /// <summary>"1 234,00 zł (23,4%)".</summary>
    public string ValueOf(ChartItem item) => Money(item.Amount) + " (" + ShareOf(item).ToString("0.#", CultureInfo.CurrentCulture) + "%)";

    /// <summary>"Name: 1 234,00 zł (23,4%)" for the bar.</summary>
    public string TipOf(ChartItem item) => Res.Format("DashBarTip", item.Name, Money(item.Amount), ShareOf(item).ToString("0.#", CultureInfo.CurrentCulture) + "%");

    private void ShowTip(int index)
    {
        var bar = _bars[index];
        // above the bar, the point of the arrow at the middle of its top
        _tip.Show(null, [new TipRow(BrushOf(bar.Item.Color), bar.Item.Name, ValueOf(bar.Item))],
            new Point(BarLeft(index) + BarWidth / 2, TopPadding + PlotHeight - HeightOf(bar)));
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.Hide();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = ItemAt(e.GetPosition(this));
        var index = item is null ? -1 : _bars.FindIndex(b => b.Item == item);
        if (index == _hover) return;
        if (index < 0) HideTip();
        else
        {
            _hover = index;
            ShowTip(index);
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        HideTip();
        InvalidateVisual();
    }
}
