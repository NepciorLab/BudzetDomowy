using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// A double click in a text box that holds an amount selects the whole amount ("1 234,56 EUR"),
/// not only the word under the cursor (which would stop at the decimal comma).
/// </summary>
public static partial class AmountBoxes
{
    [GeneratedRegex(@"^\s*[+\-−]?\s*\d[\d\s\u00A0.,]*(\s*[A-Za-z]{3})?\s*$")]
    private static partial Regex AmountShape();

    public static bool LooksLikeAmount(string? text) => !string.IsNullOrWhiteSpace(text) && AmountShape().IsMatch(text);

    private static bool _registered;

    /// <summary>Hooks every text box of the application; call once at startup.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown));
    }

    // handled on the way down, before the text box selects "the word" itself
    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is TextBox { AcceptsReturn: false } box && LooksLikeAmount(box.Text))
        {
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
    }
}
