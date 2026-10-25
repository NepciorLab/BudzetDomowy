using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;

namespace BudzetDomowy.UiTests;

/// <summary>Arrows in a date field: a day, with Shift a month, with Ctrl a year. And the renamed recurring screen.</summary>
public class DatePickerKeysTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static KeyEventArgs Press(UIElement target, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        target.RaiseEvent(e);
        return e;
    }

    [Theory]
    [InlineData(1, ModifierKeys.None, "2026-03-01")]
    [InlineData(-1, ModifierKeys.None, "2026-02-27")]
    [InlineData(1, ModifierKeys.Shift, "2026-03-28")]
    [InlineData(-1, ModifierKeys.Shift, "2026-01-28")]
    [InlineData(1, ModifierKeys.Control, "2027-02-28")]
    [InlineData(-1, ModifierKeys.Control, "2025-02-28")]
    [InlineData(1, ModifierKeys.Control | ModifierKeys.Shift, "2027-02-28")]      // Ctrl wins
    public void OneStep(int direction, ModifierKeys modifiers, string expected) =>
        Assert.Equal(DateTime.Parse(expected), DatePickerKeys.Shift(new DateTime(2026, 2, 28), direction, modifiers));

    [Fact]
    public void AMonthStepAtTheEndOfAMonth_LandsOnTheLastDayOfTheNextOne() =>
        Assert.Equal(new DateTime(2026, 2, 28), DatePickerKeys.Shift(new DateTime(2026, 1, 31), 1, ModifierKeys.Shift));

    [Fact]
    public void ThePickerMovesByTheStep_FromItsDate_OrFromWhatWasTyped_OrFromToday() => WithApp(app =>
    {
        var picker = new DatePicker { SelectedDate = new DateTime(2026, 11, 10) };

        Assert.True(DatePickerKeys.Step(picker, 1, ModifierKeys.None));
        Assert.Equal(new DateTime(2026, 11, 11), picker.SelectedDate);
        Assert.True(DatePickerKeys.Step(picker, -1, ModifierKeys.Shift));
        Assert.Equal(new DateTime(2026, 10, 11), picker.SelectedDate);
        Assert.True(DatePickerKeys.Step(picker, 1, ModifierKeys.Control));
        Assert.Equal(new DateTime(2027, 10, 11), picker.SelectedDate);

        var empty = new DatePicker();
        Assert.True(DatePickerKeys.Step(empty, 1, ModifierKeys.None));
        Assert.Equal(DateTime.Today.AddDays(1), empty.SelectedDate);

        var limited = new DatePicker { SelectedDate = new DateTime(2026, 11, 10), DisplayDateEnd = new DateTime(2026, 11, 10) };
        Assert.False(DatePickerKeys.Step(limited, 1, ModifierKeys.None));              // not beyond the allowed range
        Assert.Equal(new DateTime(2026, 11, 10), limited.SelectedDate);
    });

    [Fact]
    public void TheArrowKeys_WorkInEveryDatePicker_AndOnlyWhenTheCalendarIsClosed() => WithApp(app =>
    {
        var picker = new DatePicker { SelectedDate = new DateTime(2026, 11, 10) };
        var window = new Window { Content = picker };
        UiHost.Layout(picker, 200, 40);

        var up = Press(picker, Key.Up);
        Assert.True(up.Handled);
        Assert.Equal(new DateTime(2026, 11, 11), picker.SelectedDate);
        Assert.True(Press(picker, Key.Down).Handled);
        Assert.True(Press(picker, Key.Down).Handled);
        Assert.Equal(new DateTime(2026, 11, 9), picker.SelectedDate);

        Assert.False(Press(picker, Key.Left).Handled);                                 // other keys are left alone
        Assert.False(Press(picker, Key.Enter).Handled);
        Assert.Equal(new DateTime(2026, 11, 9), picker.SelectedDate);

        picker.IsDropDownOpen = true;                                                  // the calendar uses the arrows itself
        Assert.False(Press(picker, Key.Up).Handled);
        Assert.Equal(new DateTime(2026, 11, 9), picker.SelectedDate);
        window.Content = null;
    });

    [Fact]
    public void ARowOfAPaymentSeries_FollowsTheArrowsToo() => WithApp(app =>
    {
        app.Core.Payments.AddPayee("Tauron", "61109010140000071219812874");
        var vm = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        vm.Rows[0].DueDate = new DateTime(2026, 11, 10);
        var window = new PaymentEntryWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 760, 600);
        var picker = UiHost.Descendants<DatePicker>(content).First();

        Press(picker, Key.Up);
        Assert.Equal(new DateTime(2026, 11, 11), vm.Rows[0].DueDate);                  // the view model follows
    });

    [Fact]
    public void TheRecurringScreen_IsCalledRecurringExpenses() => WithApp(app =>
    {
        var main = app.Get<MainViewModel>();
        Assert.Equal("Wydatki cykliczne", main.NavItems.Single(n => n.Key == "recurring").Title);
        Assert.DoesNotContain(main.NavItems, n => n.Title.Contains("Wpisy cykliczne"));
    });
}
