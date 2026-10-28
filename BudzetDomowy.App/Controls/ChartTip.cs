using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BudzetDomowy.App.Controls;

/// <summary>One line of a tip: the colour of the series, its name and the figure.</summary>
public sealed record TipRow(Brush Dot, string Name, string Value);

/// <summary>
/// The tip of the charts: light, rounded, a dot in the colour of the series in front of each line and an arrow pointing at the place
/// it speaks about. It stands above that place (below it when there is no room above) and is put by the screen position of the place,
/// so it is right whatever the scale of the interface, and it moves when the pointer goes to another place instead of staying behind.
/// </summary>
public sealed class ChartTip
{
    private const double Shadow = 6;
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xE6, 0xE6, 0xE6)));
    private static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));

    private readonly FrameworkElement _target;
    private readonly Popup _popup;
    private readonly StackPanel _root = new() { Margin = new Thickness(Shadow), IsHitTestVisible = false };
    private readonly StackPanel _lines = new();
    private readonly TextBlock _heading = new() { Foreground = Text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Polygon _arrowDown = Arrow(false);
    private readonly Polygon _arrowUp = Arrow(true);

    public ChartTip(FrameworkElement target)
    {
        _target = target;
        var box = new Border { Background = Background, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Child = new StackPanel { Children = { _heading, _lines } } };
        Grid.SetIsSharedSizeScope(_lines, true);
        _root.Children.Add(_arrowUp);
        _root.Children.Add(box);
        _root.Children.Add(_arrowDown);
        _root.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 6, Opacity = 0.35 };
        _popup = new Popup
        {
            AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, PlacementTarget = target, IsHitTestVisible = false, Child = _root,
        };
    }

    public bool IsOpen => _popup.IsOpen;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Polygon Arrow(bool up) => new()
    {
        Points = up ? new PointCollection { new Point(0, 7), new Point(14, 7), new Point(7, 0) } : new PointCollection { new Point(0, 0), new Point(14, 0), new Point(7, 7) },
        Fill = Background, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, up ? 0 : -1, 0, up ? -1 : 0), Visibility = Visibility.Collapsed,
    };

    /// <summary>Shows the tip for the point <paramref name="anchor"/> (in the coordinates of the chart); false when the chart is not on a screen.</summary>
    /// <param name="preferBelow">The tip stands under the point when there is room (the ring: slices in the lower half).</param>
    public bool Show(string? heading, IReadOnlyList<TipRow> rows, Point anchor, bool preferBelow = false)
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

        Point origin, point, one;
        try
        {
            origin = _target.PointToScreen(new Point(0, 0));
            point = _target.PointToScreen(anchor);
            one = _target.PointToScreen(new Point(100, 0));
        }
        catch (InvalidOperationException)
        {
            return false;                                                           // not shown in a window (a layout in a test)
        }

        var dpi = VisualTreeHelper.GetDpi(_target);
        var scale = Math.Max(0.1, (one.X - origin.X) / 100 / dpi.DpiScaleX);         // the scale of the interface included: the tip is as big as the text around it
        _root.LayoutTransform = new ScaleTransform(scale, scale);
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _root.DesiredSize;

        var x = (point.X - origin.X) / dpi.DpiScaleX;
        var y = (point.Y - origin.Y) / dpi.DpiScaleY;
        var screenY = point.Y / dpi.DpiScaleY;
        var room = Shadow * scale;
        var below = preferBelow ? screenY + size.Height + 8 < SystemParameters.PrimaryScreenHeight : screenY - size.Height - 4 < 0;
        _arrowDown.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
        _arrowUp.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        size = _root.DesiredSize;

        _popup.HorizontalOffset = x - size.Width / 2;
        _popup.VerticalOffset = below ? y - room + 2 : y - size.Height + room - 2;     // the point of the arrow touches the place
        _popup.IsOpen = true;
        return true;
    }

    public void Hide() => _popup.IsOpen = false;
}
