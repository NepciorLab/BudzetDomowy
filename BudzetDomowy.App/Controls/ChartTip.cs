using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BudzetDomowy.App.Controls;

/// <summary>One line of a tip: the colour of the series, its name and the figure.</summary>
public sealed record TipRow(Brush Dot, string Name, string Value);

/// <summary>Where a tip stands: the top left of the whole visual (box, arrow and room for the shadow), the side of the arrow and where it starts in the box.</summary>
public readonly record struct TipPlacement(Point TopLeft, bool Below, double ArrowLeft);

/// <summary>
/// The tip of the charts: light, rounded, a dot in the colour of the series in front of each line, and an arrow whose point is exactly on
/// the place it speaks about (or no arrow, in a fixed corner of the chart). It is drawn in the adorner layer of the window, as a part
/// of the picture: it is never a window of its own, so it cannot take the pointer away from the chart (which made the old one blink),
/// it follows the scale of the interface and it is put by the coordinates of the chart itself.
/// </summary>
public sealed class ChartTip
{
    public const double Shadow = 6;
    public const double ArrowHeight = 7;
    private const double ArrowWidth = 14;
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xE6, 0xE6, 0xE6)));
    private static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));

    private sealed class TipAdorner : Adorner
    {
        private readonly FrameworkElement _child;

        public Point Position { get; set; }

        public TipAdorner(UIElement adorned, FrameworkElement child) : base(adorned)
        {
            _child = child;
            AddVisualChild(child);
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(Position, _child.DesiredSize));
            return finalSize;
        }
    }

    private readonly FrameworkElement _target;
    private readonly StackPanel _root = new() { Margin = new Thickness(Shadow), IsHitTestVisible = false };
    private readonly StackPanel _lines = new();
    private readonly TextBlock _heading = new() { Foreground = Text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Border _box;
    private readonly Polygon _arrowDown = Arrow(false);
    private readonly Polygon _arrowUp = Arrow(true);
    private TipAdorner? _adorner;
    private AdornerLayer? _layer;

    public ChartTip(FrameworkElement target)
    {
        _target = target;
        _box = new Border { Background = Background, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Child = new StackPanel { Children = { _heading, _lines } } };
        Grid.SetIsSharedSizeScope(_lines, true);
        _root.Children.Add(_arrowUp);
        _root.Children.Add(_box);
        _root.Children.Add(_arrowDown);
        _root.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 6, Opacity = 0.35 };
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Polygon Arrow(bool up) => new()
    {
        Points = up ? new PointCollection { new Point(0, ArrowHeight), new Point(ArrowWidth, ArrowHeight), new Point(ArrowWidth / 2, 0) }
                    : new PointCollection { new Point(0, 0), new Point(ArrowWidth, 0), new Point(ArrowWidth / 2, ArrowHeight) },
        Fill = Background, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, up ? 0 : -1, 0, up ? -1 : 0), Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// Where the tip goes so that the point of its arrow is on <paramref name="anchor"/>; everything in the coordinates of the chart.
    /// It stands above the point, or below it when asked or when there is no room above, and is moved sideways (the arrow stays on the
    /// point) and up or down to stay inside <paramref name="bounds"/>.
    /// </summary>
    public static TipPlacement Place(Size box, Point anchor, Rect bounds, bool preferBelow)
    {
        var aboveTop = anchor.Y - Shadow - box.Height - ArrowHeight + 1;      // top of the visual with the arrow below the box (the arrow overlaps the box by a pixel)
        var belowTop = anchor.Y - Shadow;                                      // top of the visual with the arrow above the box
        var fitsAbove = aboveTop + Shadow >= bounds.Top;
        var fitsBelow = belowTop + Shadow + ArrowHeight + box.Height <= bounds.Bottom;
        var below = preferBelow ? fitsBelow || !fitsAbove : !fitsAbove && fitsBelow;
        var top = below ? belowTop : aboveTop;

        var boxLeft = anchor.X - box.Width / 2;
        var maxLeft = bounds.Right - box.Width - 2;
        boxLeft = Math.Max(bounds.Left + 2, Math.Min(boxLeft, maxLeft));
        var arrowLeft = Math.Clamp(anchor.X - boxLeft - ArrowWidth / 2, 0, Math.Max(0, box.Width - ArrowWidth));
        return new TipPlacement(new Point(boxLeft - Shadow, top), below, arrowLeft);
    }

    private void Fill(string? heading, IReadOnlyList<TipRow> rows)
    {
        _heading.Text = heading ?? "";
        _heading.Visibility = string.IsNullOrEmpty(heading) ? Visibility.Collapsed : Visibility.Visible;
        _lines.Children.Clear();
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Name" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Value" });
            var dot = new Ellipse { Width = 12, Height = 12, Fill = row.Dot, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = row.Name, Foreground = Text, Margin = new Thickness(8, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = row.Value, Foreground = Text, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(dot);
            grid.Children.Add(name);
            grid.Children.Add(value);
            _lines.Children.Add(grid);
        }
        _arrowUp.Visibility = _arrowDown.Visibility = Visibility.Collapsed;
        _box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    /// <summary>The layer of the whole window: a tip must not be cut by a scroll viewer around the chart.</summary>
    private bool Attach()
    {
        if (_adorner is not null && _adorner.Visibility == Visibility.Collapsed) _adorner.Visibility = Visibility.Visible;
        if (_adorner is not null) return true;
        if (Window.GetWindow(_target)?.Content is not Visual content) return false;
        _layer = AdornerLayer.GetAdornerLayer(content);
        if (_layer is null) return false;
        _adorner = new TipAdorner(_target, _root);
        _layer.Add(_adorner);
        return true;
    }

    private Rect Bounds()
    {
        var layer = _layer!;
        try { return layer.TransformToDescendant(_target).TransformBounds(new Rect(0, 0, layer.ActualWidth, layer.ActualHeight)); }
        catch (InvalidOperationException) { return new Rect(-10000, -10000, 20000, 20000); }
    }

    private void Put(Point topLeft)
    {
        _adorner!.Position = topLeft;
        _adorner.InvalidateMeasure();
        _adorner.InvalidateArrange();
    }

    /// <summary>Shows the tip with the point of its arrow on <paramref name="anchor"/> (in the coordinates of the chart); false when the chart is not in a window.</summary>
    /// <param name="preferBelow">The tip stands under the point when there is room (the ring: slices in the lower half).</param>
    public bool Show(string? heading, IReadOnlyList<TipRow> rows, Point anchor, bool preferBelow = false)
    {
        if (!Attach()) return false;
        Fill(heading, rows);
        var placement = Place(_box.DesiredSize, anchor, Bounds(), preferBelow);
        _arrowDown.Visibility = placement.Below ? Visibility.Collapsed : Visibility.Visible;
        _arrowUp.Visibility = placement.Below ? Visibility.Visible : Visibility.Collapsed;
        _arrowDown.Margin = new Thickness(placement.ArrowLeft, -1, 0, 0);
        _arrowUp.Margin = new Thickness(placement.ArrowLeft, 0, 0, -1);
        Put(placement.TopLeft);
        return true;
    }

    /// <summary>Shows the tip as a plain rectangle with its top left corner at the given place of the chart, without an arrow.</summary>
    public bool ShowAt(string? heading, IReadOnlyList<TipRow> rows, Point boxTopLeft)
    {
        if (!Attach()) return false;
        Fill(heading, rows);
        Put(new Point(boxTopLeft.X - Shadow, boxTopLeft.Y - Shadow));
        return true;
    }

    public void Hide()
    {
        if (_adorner is not null) _adorner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Takes the tip out of the layer (the chart is leaving the screen).</summary>
    public void Detach()
    {
        if (_adorner is null) return;
        _layer?.Remove(_adorner);
        _adorner = null;
        _layer = null;
    }
}
