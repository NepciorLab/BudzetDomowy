using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

/// <summary>The calculator window: the keys go to <see cref="Calculator"/>, the result can be copied to the clipboard.</summary>
public sealed partial class CalculatorViewModel : ObservableObject
{
    private readonly Calculator _calc = new();
    private readonly Action<string> _copy;

    public CalculatorViewModel(Action<string>? copy = null) => _copy = copy ?? ClipboardText.Copy;

    [ObservableProperty] private string _display = "0";
    [ObservableProperty] private string _expression = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _isError;

    /// <summary>What the last copy put on the clipboard (empty until something was copied).</summary>
    public string LastCopied { get; private set; } = "";

    private void Refresh()
    {
        IsError = _calc.HasError;
        Display = _calc.Error switch
        {
            CalcError.None => _calc.Display,
            _ => Res.Get("CalcError"),
        };
        Expression = _calc.Error switch
        {
            CalcError.DivideByZero => Res.Get("CalcDivideByZero"),
            CalcError.Overflow => Res.Get("CalcOverflow"),
            _ => _calc.Expression,
        };
        Note = "";
    }

    [RelayCommand]
    private void Digit(string digit)
    {
        if (int.TryParse(digit, out var d)) _calc.Digit(d);
        Refresh();
    }

    [RelayCommand]
    private void Comma()
    {
        _calc.Comma();
        Refresh();
    }

    /// <summary>The parameter is the operator character: + - * /.</summary>
    [RelayCommand]
    private void Operator(string symbol)
    {
        if (!string.IsNullOrEmpty(symbol) && Calculator.TryParseOp(symbol[0], out var op)) _calc.Operator(op);
        Refresh();
    }

    [RelayCommand]
    private void Equal()
    {
        _calc.Equals();
        Refresh();
    }

    [RelayCommand]
    private void Negate()
    {
        _calc.Negate();
        Refresh();
    }

    [RelayCommand]
    private void Backspace()
    {
        _calc.Backspace();
        Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        _calc.Clear();
        Refresh();
    }

    /// <summary>Puts the number on the display on the clipboard (a decimal comma, no spaces), e.g. to paste into an amount field.</summary>
    [RelayCommand]
    private void CopyResult()
    {
        var text = _calc.ResultText;
        if (text.Length == 0) return;
        _copy(text);
        LastCopied = text;
        Note = Res.Format("CalcCopied", text);
    }
}
