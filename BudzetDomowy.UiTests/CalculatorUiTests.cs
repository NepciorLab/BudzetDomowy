using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;

namespace BudzetDomowy.UiTests;

/// <summary>The calculator: the button beside the entry buttons, the window, the keys, copying the result.</summary>
public class CalculatorUiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static void Type(UIElement target, string text) =>
        target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text))
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        });

    private static void Click(CalculatorViewModel vm, string key)
    {
        switch (key)
        {
            case "C": vm.ClearCommand.Execute(null); break;
            case ",": vm.CommaCommand.Execute(null); break;
            case "=": vm.EqualCommand.Execute(null); break;
            case "+" or "-" or "*" or "/": vm.OperatorCommand.Execute(key); break;
            default: vm.DigitCommand.Execute(key); break;
        }
    }

    private static void Keys(CalculatorViewModel vm, string keys)
    {
        foreach (var k in keys) Click(vm, k.ToString());
    }

    [Fact]
    public void TheMainWindowHasACalculatorButton_NextToTheEntryButtons() => WithApp(app =>
    {
        var launcher = app.Get<FakeEntryLauncher>();
        var window = new MainWindow(app.Get<MainViewModel>(), app.Get<SettingsStore>());

        var button = Assert.IsType<Button>(window.FindName("CalculatorButton"));
        Assert.Equal("Kalkulator (otwiera się w osobnym oknie)", button.ToolTip);
        Assert.Equal("Kalkulator", System.Windows.Automation.AutomationProperties.GetName(button));
        var glyph = Assert.IsType<TextBlock>(button.Content);
        Assert.Equal("", glyph.Text);
        Assert.Equal("Segoe MDL2 Assets", glyph.FontFamily.Source);

        // it sits in the same row, after the two entry buttons
        var row = (Panel)((FrameworkElement)window.FindName("NewExpenseButton")!).Parent;
        Assert.Equal(["NewExpenseButton", "NewIncomeButton", "CalculatorButton"], row.Children.OfType<FrameworkElement>().Select(c => c.Name));

        button.Command.Execute(null);
        Assert.Equal(1, launcher.CalculatorsOpened);
        Assert.Empty(launcher.Opened);                                                      // not an entry window
    });

    [Fact]
    public void TheKeys_Calculate_AndTheDisplayFollows() => WithApp(app =>
    {
        var vm = new CalculatorViewModel(_ => { });
        Assert.Equal(("0", ""), (vm.Display, vm.Expression));

        Keys(vm, "12,5+");
        Assert.Equal(("12,5", "12,5 +"), (vm.Display, vm.Expression));
        Keys(vm, "7,5=");
        Assert.Equal(("20", "12,5 + 7,5 ="), (vm.Display, vm.Expression));

        Keys(vm, "C9/4=");
        Assert.Equal("2,25", vm.Display);
        vm.NegateCommand.Execute(null);
        Assert.Equal("-2,25", vm.Display);
        vm.BackspaceCommand.Execute(null);                                                  // a result is not edited
        Assert.Equal("-2,25", vm.Display);
        vm.ClearCommand.Execute(null);
        Assert.Equal(("0", ""), (vm.Display, vm.Expression));
    });

    [Fact]
    public void DividingByZero_ShowsAnError_AndNothingIsCopied() => WithApp(app =>
    {
        var copied = new List<string>();
        var vm = new CalculatorViewModel(copied.Add);
        Keys(vm, "5/0=");
        Assert.True(vm.IsError);
        Assert.Equal(("Błąd", "Dzielenie przez zero"), (vm.Display, vm.Expression));

        vm.CopyResultCommand.Execute(null);
        Assert.Empty(copied);
        Assert.Equal("", vm.Note);

        Keys(vm, "7");
        Assert.False(vm.IsError);
        Assert.Equal("7", vm.Display);
    });

    [Fact]
    public void CopyingPutsTheNumberOnTheClipboard_WithADecimalComma() => WithApp(app =>
    {
        var copied = new List<string>();
        var vm = new CalculatorViewModel(copied.Add);
        Keys(vm, "1000*1,5=");

        vm.CopyResultCommand.Execute(null);

        Assert.Equal(["1500"], copied);                                                     // no spaces, no thousands separators
        Assert.Equal("1500", vm.LastCopied);
        Assert.Equal("Skopiowano: 1500", vm.Note);
        Keys(vm, "+");
        Assert.Equal("", vm.Note);                                                          // the note goes away with the next key

        Keys(vm, "C2,50");
        vm.CopyResultCommand.Execute(null);
        Assert.Equal(["1500", "2,5"], copied);
    });

    [Fact]
    public void TheWindow_ShowsTheKeys_AndTakesTypedCharacters() => WithApp(app =>
    {
        var copied = new List<string>();
        var vm = new CalculatorViewModel(copied.Add);
        var window = new CalculatorWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 330, 440);

        var labels = UiHost.Descendants<Button>(content).Select(b => b.Content as string).ToList();
        foreach (var key in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ",", "+", "−", "×", "÷", "=", "C" })
            Assert.Contains(key, labels);
        Assert.Contains("Kopiuj wynik", UiHost.Texts(content));
        Assert.All(UiHost.Descendants<Button>(content), b => Assert.False(b.Focusable));    // the keyboard stays with the window

        Type(window, "1");
        Type(window, "2");
        Type(window, ",");
        Type(window, "5");
        Type(window, "*");
        Type(window, "2");
        Type(window, "=");
        Assert.Equal("25", vm.Display);

        Type(window, "-");
        Type(window, "5");
        Type(window, "/");
        Type(window, "4");
        Type(window, "=");
        Assert.Equal("5", vm.Display);                                                      // (25 − 5) ÷ 4

        Type(window, ".");                                                                  // the dot works as a comma too
        Type(window, "a");                                                                  // letters do nothing
        Assert.Equal("0,", vm.Display);

        var copy = UiHost.Descendants<Button>(content).Single(b => b.Name == "CopyButton");
        copy.Command.Execute(null);
        Assert.Equal(["0"], copied);

        window.Content = null;
        content.DataContext = vm;
        UiHost.Render(content, 330, 500, "calculator");
    });

    [Fact]
    public void ALongNumber_ShrinksToFitTheDisplay() => WithApp(app =>
    {
        var vm = new CalculatorViewModel(_ => { });
        var window = new CalculatorWindow(vm);
        var content = (FrameworkElement)window.Content;
        Keys(vm, "999999999999999*999999999999999=");
        UiHost.Layout(content, 330, 440);
        var viewbox = UiHost.Descendants<Viewbox>(content).Single();
        Assert.True(((FrameworkElement)viewbox.Child).ActualWidth > 0);
        Assert.True(viewbox.ActualWidth <= 330);
    });
}
