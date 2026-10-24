using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Controls;

/// <summary>A small coloured symbol (circle, square or vertical bar, as chosen in Settings) that marks a category.</summary>
public sealed class CategoryMarker : Border
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(CategoryMarker), new PropertyMetadata("", (d, _) => ((CategoryMarker)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(CategoryMarker), new PropertyMetadata(10.0, (d, _) => ((CategoryMarker)d).Update()));

    /// <summary>When set, the shape is this one instead of the chosen one (previews in Settings).</summary>
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(MarkerShape?), typeof(CategoryMarker), new PropertyMetadata(null, (d, _) => ((CategoryMarker)d).Update()));

    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public MarkerShape? Shape { get => (MarkerShape?)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }

    public CategoryMarker()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) =>
        {
            UiPreferences.Instance.PropertyChanged += OnPreferencesChanged;
            Update();
        };
        Unloaded += (_, _) => UiPreferences.Instance.PropertyChanged -= OnPreferencesChanged;
        Update();
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UiPreferences.Marker)) Update();
    }

    private void Update()
    {
        var shape = Shape ?? UiPreferences.Instance.Marker;
        switch (shape)
        {
            case MarkerShape.Square:
                Width = Size; Height = Size; CornerRadius = new CornerRadius(Math.Max(1, Size / 6));
                break;
            case MarkerShape.Bar:
                Width = Math.Max(3, Size * 0.38); Height = Size * 1.6; CornerRadius = new CornerRadius(Math.Max(1, Size / 8));
                break;
            default:
                Width = Size; Height = Size; CornerRadius = new CornerRadius(Size / 2);
                break;
        }

        Background = Color.Length > 0 && TryBrush(Color) is { } brush ? brush : Brushes.Transparent;
    }

    private static Brush? TryBrush(string hex)
    {
        try { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return null; }
    }
}
