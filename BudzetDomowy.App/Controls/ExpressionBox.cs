using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// A box for an amount that may be a calculation ("2+2*2"). While it is being edited it shows what was typed; when it is left
/// (or <see cref="Commit"/> is called) the typed text is handed to <see cref="Expression"/> and the box shows the result
/// (<see cref="DisplayText"/>). Coming back to the box shows the calculation again, selected.
/// </summary>
public sealed class ExpressionBox : TextBox
{
    public static readonly DependencyProperty ExpressionProperty = DependencyProperty.Register(
        nameof(Expression), typeof(string), typeof(ExpressionBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ExpressionBox)d).Refresh()));

    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
        nameof(DisplayText), typeof(string), typeof(ExpressionBox), new PropertyMetadata("", (d, _) => ((ExpressionBox)d).Refresh()));

    /// <summary>What the user typed (the calculation itself).</summary>
    public string Expression { get => (string)GetValue(ExpressionProperty); set => SetValue(ExpressionProperty, value); }

    /// <summary>The result to show when the box is not being edited; empty shows the typed text as it is.</summary>
    public string DisplayText { get => (string)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }

    private bool _editing;

    public ExpressionBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));          // looks like every other text box of the program
        GotKeyboardFocus += (_, _) =>
        {
            _editing = true;
            Text = Expression;
            Dispatcher.BeginInvoke(SelectAll, System.Windows.Threading.DispatcherPriority.Input);
        };
        LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject target && IsAncestorOf(target)) return;
            Commit();
            _editing = false;
            Refresh();
        };
        Refresh();
    }

    /// <summary>Hands what was typed over (the owner calculates it) and shows the result.</summary>
    public void Commit()
    {
        if (_editing && Text != Expression) SetCurrentValue(ExpressionProperty, Text);
        _editing = false;                       // editing is over; a later FocusAndSelect (or a click) starts it again
        Refresh();
    }

    private void Refresh(bool forceResult = false)
    {
        if (_editing && !forceResult) return;
        var shown = string.IsNullOrEmpty(DisplayText) ? Expression : DisplayText;
        if (Text != shown) Text = shown;
    }

    /// <summary>Puts the keyboard in the box with the whole text selected.</summary>
    public void FocusAndSelect()
    {
        Focus();
        Keyboard.Focus(this);
        _editing = true;
        Text = Expression;
        SelectAll();
    }
}
