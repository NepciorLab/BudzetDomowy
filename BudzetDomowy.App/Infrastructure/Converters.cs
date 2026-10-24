using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BudzetDomowy.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Shared instance for use with {x:Static} where no resource is declared.</summary>
    public static readonly BoolToVisibilityConverter Visible = new();

    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is not null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" text to a brush; empty or invalid gives a transparent brush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && s.Length > 0)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
            catch (FormatException) { }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A width in pixels to a left margin (indentation of tree-like lists).</summary>
public sealed class IndentConverter : IValueConverter
{
    public static readonly IndentConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
