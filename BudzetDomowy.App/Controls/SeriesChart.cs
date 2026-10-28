using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using BudzetDomowy.App.Infrastructure;
using LiveChartsCore;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// Columns and lines over categories, drawn by WPF itself (sharp, in the font of the program, and the same on every scale of the
/// interface). It reads the series and the axes the view models already build for LiveCharts (names, values, colours, labels, limits),
/// so the screens keep their data and only the drawing is different. A legend stands under the plot; pointing at a category shows a
/// tip with its heading and one line per series above the highest value of it.
/// </summary>
public sealed class SeriesChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ISeries>), typeof(SeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SeriesChart)d).Rebuild()));

    public static readonly DependencyProperty XAxesProperty = DependencyProperty.Register(
        nameof(XAxes), typeof(IEnumerable<ICartesianAxis>), typeof(SeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SeriesChart)d).Rebuild()));

    public static readonly DependencyProperty YAxesProperty = DependencyProperty.Register(
        nameof(YAxes), typeof(IEnumerable<ICartesianAxis>), typeof(SeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SeriesChart)d).Rebuild()));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(SeriesChart), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public IEnumerable<ISeries>? Series { get => (IEnumerable<ISeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    public IEnumerable<ICartesianAxis>? XAxes { get => (IEnumerable<ICartesianAxis>?)GetValue(XAxesProperty); set => SetValue(XAxesProperty, value); }

    public IEnumerable<ICartesianAxis>? YAxes { get => (IEnumerable<ICartesianAxis>?)GetValue(YAxesProperty); set => SetValue(YAxesProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private sealed class Line
    {
        public string Name = "";
        public Color Color;
        public bool IsLine;
        public double?[] Values = [];
        public double MaxBar = 40, Marker, Stroke = 2.5;
        public bool ShowValues;
        public Func<int, string>? Heading;
    }

    private List<Line> _lines = [];
    private string[] _labels = [];
    private double _labelRotation;
    private Axis? _yAxis;

    // what the last drawing used, for the pointer
    private Rect _plot;
    private int _count;
    private double _yMin, _yMax;
    private int _hover = -1;
    private readonly ChartTip _tip;
    private readonly Dictionary<Color, Brush> _brushes = [];

    public SeriesChart()
    {
        _tip = new ChartTip(this);
        Unloaded += (_, _) => _tip.Hide();
    }

    // ----- the data -----

    private static Color Of(SkiaSharp.SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    private static Color ColorOf(object? paint, Color fallback) =>
        paint is SolidColorPaint solid ? Of(solid.Color) : fallback;

    private static double?[] Numbers(IEnumerable<double>? values) => values?.Select(v => (double?)v).ToArray() ?? [];

    private static double?[] Numbers(IEnumerable<double?>? values) => values?.ToArray() ?? [];

    private void Rebuild()
    {
        var lines = new List<Line>();
        foreach (var s in Series ?? [])
        {
            switch (s)
            {
                case ColumnSeries<double> c:
                    lines.Add(new Line { Name = c.Name ?? "", Color = ColorOf(c.Fill, Colors.Gray), Values = Numbers(c.Values), MaxBar = c.MaxBarWidth, ShowValues = c.DataLabelsPaint is not null });
                    break;
                case ColumnSeries<double?> c:
                    lines.Add(new Line { Name = c.Name ?? "", Color = ColorOf(c.Fill, Colors.Gray), Values = Numbers(c.Values), MaxBar = c.MaxBarWidth, ShowValues = c.DataLabelsPaint is not null });
                    break;
                case LineSeries<double> l:
                    lines.Add(new Line
                    {
                        Name = l.Name ?? "", Color = ColorOf(l.Stroke, Colors.Gray), IsLine = true, Values = Numbers(l.Values), Marker = l.GeometrySize,
                        Stroke = (l.Stroke as SolidColorPaint)?.StrokeThickness ?? 2.5, Heading = (l as PeriodLineSeries)?.PeriodOf,
                    });
                    break;
                case LineSeries<double?> l:
                    lines.Add(new Line
                    {
                        Name = l.Name ?? "", Color = ColorOf(l.Stroke, Colors.Gray), IsLine = true, Values = Numbers(l.Values), Marker = l.GeometrySize,
                        Stroke = (l.Stroke as SolidColorPaint)?.StrokeThickness ?? 2.5,
                    });
                    break;
            }
        }
        _lines = lines;
        var x = XAxes?.OfType<Axis>().FirstOrDefault();
        _labels = x?.Labels?.ToArray() ?? [];
        _labelRotation = x?.LabelsRotation ?? 0;
        _yAxis = YAxes?.OfType<Axis>().FirstOrDefault();
        _hover = -1;
        _tip.Hide();
        InvalidateVisual();
    }

    // ----- scale -----

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1d, 2d, 2.5d, 5d, 10d }.Select(f => f * magnitude).First(s => s >= raw - 1e-9);
    }

    private void Scale(double plotHeight, out double min, out double max, out double step)
    {
        var values = _lines.SelectMany(l => l.Values).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var dataMax = Math.Max(0, values.Count == 0 ? 0 : values.Max());
        var dataMin = Math.Min(0, values.Count == 0 ? 0 : values.Min());
        var span = Math.Max(1e-9, (_yAxis?.MaxLimit ?? dataMax) - (_yAxis?.MinLimit ?? dataMin));
        step = NiceStep(span / Math.Clamp(plotHeight / 42, 2, 8));
        min = _yAxis?.MinLimit ?? (dataMin < 0 ? Math.Floor(dataMin / step) * step : 0);
        max = _yAxis?.MaxLimit ?? Math.Ceiling(dataMax / step - 1e-9) * step;
        if (max <= min) max = min + step;
    }

    // ----- layout and drawing -----

    private FormattedText Text(string text, Brush brush, double factor = 0.9)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this));
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, TextElement.GetFontSize(this) * factor, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private Brush BrushOf(Color color)
    {
        if (_brushes.TryGetValue(color, out var cached)) return cached;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return _brushes[color] = brush;
    }

    private string Label(double value) => _yAxis?.Labeler?.Invoke(value) ?? (Math.Round(value) + 0.0).ToString("N0", CultureInfo.CurrentCulture);

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 400 : available.Width, double.IsInfinity(available.Height) ? 260 : available.Height);

    /// <summary>The legend: names with their colour dots in rows that wrap, centred under the plot. Returns its height.</summary>
    private double Legend(DrawingContext? dc, double width, double top, Brush fg)
    {
        var named = _lines.Where(l => l.Name.Length > 0).ToList();
        if (named.Count == 0) return 0;
        var items = named.Select(l => (Line: l, Text: Text(l.Name, fg))).ToList();
        var rows = new List<List<(Line Line, FormattedText Text)>> { new() };
        var rowWidth = 0.0;
        foreach (var item in items)
        {
            var w = 16 + item.Text.WidthIncludingTrailingWhitespace + 16;
            if (rowWidth + w > width && rows[^1].Count > 0) { rows.Add([]); rowWidth = 0; }
            rows[^1].Add(item);
            rowWidth += w;
        }

        var y = top + 4;
        foreach (var row in rows)
        {
            var total = row.Sum(i => 16 + i.Text.WidthIncludingTrailingWhitespace + 16) - 16;
            var x = (width - total) / 2;
            foreach (var (line, text) in row)
            {
                if (dc is not null)
                {
                    dc.DrawEllipse(BrushOf(line.Color), null, new Point(x + 5, y + text.Height / 2), 5, 5);
                    dc.DrawText(text, new Point(x + 16, y));
                }
                x += 16 + text.WidthIncludingTrailingWhitespace + 16;
            }
            y += row.Max(i => i.Text.Height) + 4;
        }
        return y - top;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        _count = _lines.Count == 0 ? 0 : _lines.Max(l => l.Values.Length);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));                  // the whole chart reacts to the pointer
        if (_lines.Count == 0 || _count == 0 || w < 40 || h < 40) return;
        var fg = Foreground;

        var legendHeight = Legend(null, w, 0, fg);
        var labelHeight = _labels.Where(s => s.Length > 0).Select(s => Text(s, fg)).DefaultIfEmpty(null)
            .Max(t => t is null ? 0 : _labelRotation == 0 ? t.Height : t.WidthIncludingTrailingWhitespace * Math.Abs(Math.Sin(_labelRotation * Math.PI / 180)) + t.Height);
        var top = 14.0;
        var bottom = h - legendHeight - labelHeight - 10;
        Scale(bottom - top, out _yMin, out _yMax, out var step);

        var ticks = new List<double>();
        for (var t = Math.Ceiling(_yMin / step - 1e-9) * step; t <= _yMax + step * 1e-6; t += step) ticks.Add(Math.Round(t / step) * step + 0.0);                 // + 0.0: no "-0" for zero
        var left = ticks.Max(t => Text(Label(t), fg).WidthIncludingTrailingWhitespace) + 14;
        _plot = new Rect(left, top, Math.Max(10, w - left - 8), Math.Max(10, bottom - top));

        double Y(double v) => _plot.Bottom - (v - _yMin) / (_yMax - _yMin) * _plot.Height;
        var slot = _plot.Width / _count;
        double CentreOf(int i) => _plot.Left + slot * (i + 0.5);

        // grid and the figures of the scale
        var showGrid = _yAxis?.SeparatorsPaint is not null;
        var pen = new Pen(fg, 1);
        foreach (var t in ticks)
        {
            var y = Math.Floor(Y(t)) + 0.5;
            if (showGrid)
            {
                dc.PushOpacity(Math.Abs(t) < 1e-9 && _yMin < 0 ? 0.45 : 0.18);
                dc.DrawLine(pen, new Point(_plot.Left, y), new Point(_plot.Right, y));
                dc.Pop();
            }
            var text = Text(Label(t), fg);
            dc.PushOpacity(0.7);
            dc.DrawText(text, new Point(_plot.Left - 8 - text.WidthIncludingTrailingWhitespace, Y(t) - text.Height / 2));
            dc.Pop();
        }

        // the category under the pointer
        if (_hover >= 0 && _hover < _count)
        {
            dc.PushOpacity(0.09);
            dc.DrawRectangle(fg, null, new Rect(_plot.Left + slot * _hover, _plot.Top, slot, _plot.Height));
            dc.Pop();
        }

        // columns
        var columns = _lines.Where(l => !l.IsLine).ToList();
        if (columns.Count > 0)
        {
            const double pad = 3;
            var width = Math.Min(columns.Min(c => c.MaxBar), (slot * 0.84 - (columns.Count - 1) * pad) / columns.Count);
            width = Math.Max(1, width);
            var group = columns.Count * width + (columns.Count - 1) * pad;
            var zero = Y(Math.Clamp(0, _yMin, _yMax));
            for (var i = 0; i < _count; i++)
            {
                for (var k = 0; k < columns.Count; k++)
                {
                    if (i >= columns[k].Values.Length || columns[k].Values[i] is not { } v) continue;
                    var x = CentreOf(i) - group / 2 + k * (width + pad);
                    var y = Y(Math.Clamp(v, _yMin, _yMax));
                    var rect = new Rect(x, Math.Min(y, zero), width, Math.Max(0.5, Math.Abs(zero - y)));
                    dc.DrawRectangle(BrushOf(columns[k].Color), null, rect);
                    if (columns[k].ShowValues)
                    {
                        var label = Text(v.ToString("N0", CultureInfo.CurrentCulture), fg);
                        dc.DrawText(label, new Point(rect.Left + width / 2 - label.WidthIncludingTrailingWhitespace / 2, v >= 0 ? rect.Top - label.Height - 2 : rect.Bottom + 2));
                    }
                }
            }
        }

        // lines (a gap in the values breaks the line)
        foreach (var line in _lines.Where(l => l.IsLine))
        {
            var brush = BrushOf(line.Color);
            var linePen = new Pen(brush, line.Stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            linePen.Freeze();
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var open = false;
                for (var i = 0; i < line.Values.Length; i++)
                {
                    if (line.Values[i] is not { } v) { open = false; continue; }
                    var p = new Point(CentreOf(i), Y(Math.Clamp(v, _yMin, _yMax)));
                    if (open) g.LineTo(p, true, false); else g.BeginFigure(p, false, false);
                    open = true;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, linePen, geometry);
            if (line.Marker > 0)
                for (var i = 0; i < line.Values.Length; i++)
                    if (line.Values[i] is { } v) dc.DrawEllipse(brush, null, new Point(CentreOf(i), Y(Math.Clamp(v, _yMin, _yMax))), line.Marker / 2, line.Marker / 2);
        }

        // the labels of the categories: a label that would touch the one before it is left out
        var lastRight = double.NegativeInfinity;
        var angle = Math.Abs(_labelRotation * Math.PI / 180);
        for (var i = 0; i < _count && i < _labels.Length; i++)
        {
            if (_labels[i].Length == 0) continue;
            var text = Text(_labels[i], fg);
            var extent = _labelRotation == 0 ? text.WidthIncludingTrailingWhitespace : text.Height / Math.Max(0.2, Math.Sin(angle));
            var from = _labelRotation == 0 ? CentreOf(i) - extent / 2 : CentreOf(i);
            if (from < lastRight + 4) continue;
            lastRight = from + extent;
            if (_labelRotation == 0)
            {
                dc.DrawText(text, new Point(from, _plot.Bottom + 6));
            }
            else
            {
                dc.PushTransform(new RotateTransform(_labelRotation, CentreOf(i), _plot.Bottom + 6));
                dc.DrawText(text, new Point(CentreOf(i), _plot.Bottom + 6));
                dc.Pop();
            }
        }

        Legend(dc, w, h - legendHeight, fg);
    }

    // ----- pointer -----

    private static string Money(double v) => ((decimal)v).ToString("C2", CultureInfo.CurrentCulture);

    /// <summary>The rows of the tip of a category: every series that has a value there.</summary>
    public IReadOnlyList<(string Name, string Value)> RowsAt(int index) =>
        _lines.Where(l => index < l.Values.Length && l.Values[index].HasValue).Select(l => (l.Name, Money(l.Values[index]!.Value))).ToList();

    /// <summary>The heading of the tip of a category: the period the lines tell, or the label of the category.</summary>
    public string HeadingAt(int index) =>
        _lines.Select(l => l.Heading).FirstOrDefault(f => f is not null)?.Invoke(index) ?? (index < _labels.Length ? _labels[index] : "");

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var index = _count > 0 && _plot.Contains(p) ? Math.Clamp((int)((p.X - _plot.Left) / (_plot.Width / _count)), 0, _count - 1) : -1;
        if (index == _hover) return;
        _hover = index;
        var rows = index < 0 ? [] : _lines.Where(l => index < l.Values.Length && l.Values[index].HasValue).ToList();
        if (rows.Count == 0)
        {
            _hover = index < 0 ? -1 : index;
            _tip.Hide();
        }
        else
        {
            var highest = rows.Max(l => l.Values[index]!.Value);
            var y = _plot.Bottom - (Math.Clamp(highest, _yMin, _yMax) - _yMin) / (_yMax - _yMin) * _plot.Height;
            var x = _plot.Left + _plot.Width / _count * (index + 0.5);
            _tip.Show(HeadingAt(index), rows.Select(l => new TipRow(BrushOf(l.Color), l.Name, Money(l.Values[index]!.Value))).ToList(), new Point(x, y));
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
