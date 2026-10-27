using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Controls;

/// <summary>One bar of <see cref="BarBreakdownChart"/>: a category, its colour (#RRGGBB) and the amount; a change of the amount moves the bar.</summary>
public sealed class BarItem : INotifyPropertyChanged
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
/// Columns of the expenses by category: the chart is put at the left edge, every bar has a slot twice as wide as itself, so the
/// horizontal lines of the grid reach half a bar beyond the first and the last one and end there instead of running over the whole
/// card. The names stand under the bars, turned upright. Another amount moves the bar (and the scale) smoothly; a bar that comes
/// new grows from the bottom. Pointing at a bar shows its amount and its share.
/// </summary>
public sealed class BarBreakdownChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<BarItem>), typeof(BarBreakdownChart),
        new FrameworkPropertyMetadata(null, (d, e) => ((BarBreakdownChart)d).OnItemsReplaced(e.OldValue as IEnumerable<BarItem>, e.NewValue as IEnumerable<BarItem>)));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(BarBreakdownChart), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public IEnumerable<BarItem>? Items { get => (IEnumerable<BarItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public const double PlotHeight = 150;
    private const double TopPadding = 10, LabelGap = 6, MaxLabelHeight = 110, MaxBarWidth = 30, MinBarWidth = 8, GridLines = 4;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(450);

    private sealed class Bar
    {
        public required BarItem Item { get; init; }
        public double From, To, Shown;
    }

    private List<Bar> _bars = [];
    private double _fromMax, _toMax, _shownMax;
    private DateTime _started;
    private bool _animating;
    private double _axisWidth, _barWidth = MaxBarWidth, _labelHeight;
    private int _hover = -1;
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Controls.ToolTip _tip = new();

    public BarBreakdownChart()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTip = _tip;                                                                    // one tip for good: its text follows the bar under the pointer
        ToolTipOpening += (_, e) => e.Handled = _hover < 0;
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>Width of the slot of one bar; the bar itself is half of it.</summary>
    public double SlotWidth => _barWidth * 2;

    public double BarWidth => _barWidth;

    /// <summary>The horizontal lines of the grid run from here to <see cref="PlotRight"/>.</summary>
    public double PlotLeft => _axisWidth;

    public double PlotRight => _axisWidth + SlotWidth * _bars.Count;

    // ----- the items -----

    private void OnItemsReplaced(IEnumerable<BarItem>? oldItems, IEnumerable<BarItem>? newItems)
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

        _fromMax = _shownMax;
        _toMax = NiceMax(bars.Count == 0 ? 0 : bars.Max(b => b.To));
        if (_shownMax <= 0) _shownMax = _fromMax = _toMax;                                 // the scale is there from the start, only the bars grow

        _hover = -1;
        _tip.IsOpen = false;
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

    private static string Tick(double value) => Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);

    protected override Size MeasureOverride(Size available)
    {
        var n = _bars.Count;
        if (n == 0) return default;

        var brush = Foreground;
        _axisWidth = Enumerable.Range(0, (int)GridLines + 1).Max(k => Text(Tick(_toMax * k / GridLines), brush).WidthIncludingTrailingWhitespace) + 10;
        _barWidth = double.IsInfinity(available.Width)
            ? MaxBarWidth
            : Math.Clamp((available.Width - _axisWidth - 2) / (2 * n), MinBarWidth, MaxBarWidth);
        _labelHeight = Math.Min(MaxLabelHeight, _bars.Max(b => Text(b.Item.Name, brush).WidthIncludingTrailingWhitespace) + 4);
        return new Size(_axisWidth + SlotWidth * n, TopPadding + PlotHeight + LabelGap + _labelHeight);
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

    private double HeightOf(Bar bar) => _shownMax > 0 ? Math.Min(PlotHeight, PlotHeight * bar.Shown / _shownMax) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var n = _bars.Count;
        if (n == 0) return;
        var fg = Foreground;
        var bottom = TopPadding + PlotHeight;
        var slot = SlotWidth;

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
            var left = PlotLeft + slot * i;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(left, TopPadding, slot, PlotHeight + LabelGap + _labelHeight));     // the whole slot reacts to the pointer

            var h = HeightOf(bar);
            if (h > 0)
            {
                var rect = new Rect(left + _barWidth / 2, bottom - h, _barWidth, h);
                dc.DrawRectangle(BrushOf(bar.Item.Color), null, rect);
                if (i == _hover)
                {
                    dc.PushOpacity(0.22);
                    dc.DrawRectangle(Brushes.White, null, rect);
                    dc.Pop();
                }
            }

            // the name, upright under the bar and starting at the axis
            var name = Text(bar.Item.Name, fg, _labelHeight);
            var matrix = Matrix.Identity;
            matrix.Rotate(90);
            matrix.Translate(left + slot / 2 + name.Height / 2, bottom + LabelGap);
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawText(name, new Point(0, 0));
            dc.Pop();
        }
    }

    // ----- pointer -----

    /// <summary>The bar at the point: its column, from the top of the bar down to the end of the name.</summary>
    public BarItem? ItemAt(Point p)
    {
        var n = _bars.Count;
        if (n == 0 || p.X < PlotLeft || p.X >= PlotRight) return null;
        var i = (int)((p.X - PlotLeft) / SlotWidth);
        var bottom = TopPadding + PlotHeight;
        var top = bottom - Math.Max(HeightOf(_bars[i]), 0);
        return p.Y >= top && p.Y <= bottom + LabelGap + _labelHeight ? _bars[i].Item : null;
    }

    /// <summary>"Name: 1 234,00 zł (23,4%)" for the bar.</summary>
    public string TipOf(BarItem item)
    {
        var total = _bars.Sum(b => b.To);
        var share = total > 0 ? item.Amount / total * 100 : 0;
        return Res.Format("DashBarTip", item.Name, ((decimal)item.Amount).ToString("C2", CultureInfo.CurrentCulture),
            share.ToString("0.#", CultureInfo.CurrentCulture) + "%");
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = ItemAt(e.GetPosition(this));
        var index = item is null ? -1 : _bars.FindIndex(b => b.Item == item);
        if (index == _hover) return;
        _hover = index;
        if (item is null) _tip.IsOpen = false; else _tip.Content = TipOf(item);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }
}
