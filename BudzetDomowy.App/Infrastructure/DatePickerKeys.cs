using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// In every date field the arrows change the date: Up and Down by one day, with Shift by one month, with Ctrl by one year
/// (Up goes forward). With the calendar open the arrows move inside it as usual, and Alt+Down still opens it.
/// </summary>
public static class DatePickerKeys
{
    private static bool _registered;

    /// <summary>Hooks every date picker of the application; call once at startup.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(DatePicker), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || sender is not DatePicker { IsDropDownOpen: false } picker) return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        if (Step(picker, e.Key == Key.Up ? 1 : -1, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>The date after one step: a day, with Shift a month, with Ctrl a year (Ctrl wins when both are held).</summary>
    public static DateTime Shift(DateTime date, int direction, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) != 0) return date.AddYears(direction);
        if ((modifiers & ModifierKeys.Shift) != 0) return date.AddMonths(direction);
        return date.AddDays(direction);
    }

    /// <summary>Moves the picker's date by one step; the typed text counts even if it was not accepted yet. False when it cannot move.</summary>
    public static bool Step(DatePicker picker, int direction, ModifierKeys modifiers)
    {
        var current = DateTime.TryParse(picker.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var typed)
            ? typed.Date
            : picker.SelectedDate?.Date ?? DateTime.Today;
        DateTime next;
        try { next = Shift(current, direction, modifiers); }
        catch (ArgumentOutOfRangeException) { return false; }          // beyond the first or last year the calendar knows

        if (picker.DisplayDateStart is { } start && next < start) return false;
        if (picker.DisplayDateEnd is { } end && next > end) return false;
        picker.SelectedDate = next;
        return true;
    }
}
