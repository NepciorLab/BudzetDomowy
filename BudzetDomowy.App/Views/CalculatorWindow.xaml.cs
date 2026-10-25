using System.Windows;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

/// <summary>A small calculator in its own window; it can be left open next to the main window.</summary>
public partial class CalculatorWindow : Window
{
    private readonly CalculatorViewModel _vm;

    public CalculatorWindow(CalculatorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        Loaded += (_, _) => Focus();
        PreviewTextInput += OnTextInput;
        PreviewKeyDown += OnKey;
    }

    /// <summary>The typed character decides, so it works on every keyboard layout and on the numeric keypad.</summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
        {
            if (c is >= '0' and <= '9') _vm.DigitCommand.Execute(c.ToString());
            else if (c is ',' or '.') _vm.CommaCommand.Execute(null);
            else if (c == '=') _vm.EqualCommand.Execute(null);
            else if (BudzetDomowy.Core.Services.Calculator.TryParseOp(c, out _) && c is not ('x' or 'X')) _vm.OperatorCommand.Execute(c.ToString());
            else continue;
            e.Handled = true;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm.EqualCommand.Execute(null);
                break;
            case Key.Back:
                _vm.BackspaceCommand.Execute(null);
                break;
            case Key.Delete:
                _vm.ClearCommand.Execute(null);
                break;
            case Key.Escape:
                Close();
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.CopyResultCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
