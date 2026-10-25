using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// A bar with two handles: the left one sets the lower value, the right one the upper value, and the part between them is highlighted.
/// The values are whole numbers (here: days). With a handle focused the arrows move it by one, Shift by 30 and Ctrl by 365 (a day, a
/// month, a year in the dates it is used for); Home and End send it to the end of the bar. A click on the bar moves the nearer handle.
/// </summary>
public partial class RangeSlider : UserControl
{
    private const double HandleWidth = 20;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0d, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(100d, OnRangeChanged));

    public static readonly DependencyProperty LowerValueProperty = DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceLower));

    public static readonly DependencyProperty UpperValueProperty = DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceUpper));

    /// <summary>Turns a value into the text of the handle's tool tip (a date, for example).</summary>
    public static readonly DependencyProperty ToolTipConverterProperty = DependencyProperty.Register(nameof(ToolTipConverter), typeof(System.Windows.Data.IValueConverter), typeof(RangeSlider),
        new PropertyMetadata(null, (d, _) => ((RangeSlider)d).UpdateToolTips()));

    private double _dragStart;
    private double _dragTotal;

    public RangeSlider()
    {
        InitializeComponent();
        LowerThumb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Początek zakresu");
        UpperThumb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Koniec zakresu");
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double LowerValue
    {
        get => (double)GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    public double UpperValue
    {
        get => (double)GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    public System.Windows.Data.IValueConverter? ToolTipConverter
    {
        get => (System.Windows.Data.IValueConverter?)GetValue(ToolTipConverterProperty);
        set => SetValue(ToolTipConverterProperty, value);
    }

    // the lower value may not pass the upper one and the other way round; both stay inside the bar
    private static object CoerceLower(DependencyObject d, object value)
    {
        var s = (RangeSlider)d;
        return Math.Round(Math.Clamp((double)value, s.Minimum, Math.Max(s.Minimum, s.UpperValue)));
    }

    private static object CoerceUpper(DependencyObject d, object value)
    {
        var s = (RangeSlider)d;
        return Math.Round(Math.Clamp((double)value, Math.Min(s.Maximum, s.LowerValue), s.Maximum));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (RangeSlider)d;
        s.CoerceValue(UpperValueProperty);
        s.CoerceValue(LowerValueProperty);
        s.PlaceHandles();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (RangeSlider)d;
        if (e.Property == LowerValueProperty) s.CoerceValue(UpperValueProperty);
        else s.CoerceValue(LowerValueProperty);
        s.PlaceHandles();
    }

    private double Span => Math.Max(1e-9, Maximum - Minimum);

    private double Usable => Math.Max(1, Root.ActualWidth - HandleWidth);

    private double PositionOf(double value) => (value - Minimum) / Span * Usable;

    private double ValueAt(double x) => Minimum + Math.Clamp(x / Usable, 0, 1) * Span;

    private void PlaceHandles()
    {
        if (!IsLoaded && Root.ActualWidth <= 0) return;
        var lower = PositionOf(LowerValue);
        var upper = PositionOf(UpperValue);
        Canvas.SetLeft(LowerThumb, lower);
        Canvas.SetLeft(UpperThumb, upper);
        Selection.Margin = new Thickness(lower + HandleWidth / 2, 0, 0, 0);
        Selection.Width = Math.Max(0, upper - lower);
        UpdateToolTips();
    }

    private void UpdateToolTips()
    {
        string? Text(double v) => ToolTipConverter?.Convert(v, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture)?.ToString();
        LowerThumb.ToolTip = Text(LowerValue);
        UpperThumb.ToolTip = Text(UpperValue);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceHandles();

    // ----- dragging -----

    private void Lower_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragStart = LowerValue;
        _dragTotal = 0;
    }

    private void Lower_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _dragTotal += e.HorizontalChange;
        LowerValue = _dragStart + _dragTotal / Usable * Span;
    }

    private void Upper_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragStart = UpperValue;
        _dragTotal = 0;
    }

    private void Upper_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _dragTotal += e.HorizontalChange;
        UpperValue = _dragStart + _dragTotal / Usable * Span;
    }

    // ----- a click on the bar: the nearer handle jumps there -----

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsHandle(source)) return;
        var value = ValueAt(e.GetPosition(Root).X - HandleWidth / 2);
        var lowerNearer = Math.Abs(value - LowerValue) <= Math.Abs(value - UpperValue);
        if (Math.Abs(value - LowerValue) == Math.Abs(value - UpperValue)) lowerNearer = value < LowerValue;     // in the middle of an empty gap: towards the click
        if (lowerNearer) { LowerValue = value; LowerThumb.Focus(); }
        else { UpperValue = value; UpperThumb.Focus(); }
        e.Handled = true;
    }

    private bool IsHandle(DependencyObject source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, LowerThumb) || ReferenceEquals(d, UpperThumb)) return true;
        return false;
    }

    // ----- the keyboard -----

    private void Thumb_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var lower = ReferenceEquals(sender, LowerThumb);
        var step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 365 : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 30 : 1;
        double? target = e.Key switch
        {
            Key.Left or Key.Down => (lower ? LowerValue : UpperValue) - step,
            Key.Right or Key.Up => (lower ? LowerValue : UpperValue) + step,
            Key.PageDown => (lower ? LowerValue : UpperValue) - 30,
            Key.PageUp => (lower ? LowerValue : UpperValue) + 30,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (target is not { } value) return;
        if (lower) LowerValue = value; else UpperValue = value;
        e.Handled = true;
    }

    /// <summary>The position of a handle's left edge, in the pixels of the bar.</summary>
    public double LowerHandleLeft => Canvas.GetLeft(LowerThumb);

    public double UpperHandleLeft => Canvas.GetLeft(UpperThumb);

    public Thumb LowerHandle => LowerThumb;

    public Thumb UpperHandle => UpperThumb;
}
