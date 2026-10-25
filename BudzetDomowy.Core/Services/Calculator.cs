using System.Globalization;

namespace BudzetDomowy.Core.Services;

public enum CalcOp { Add, Subtract, Multiply, Divide }

public enum CalcError { None, DivideByZero, Overflow }

/// <summary>
/// Pocket calculator: digits, comma, + − × ÷, executed one operation at a time (12 + 3 × 2 = 30, as on a desk calculator).
/// Pure logic, no UI: the window only forwards the keys and shows <see cref="Display"/>.
/// </summary>
public sealed class Calculator
{
    public const int MaxDigits = 15;
    private const int ResultDecimals = 10;

    private decimal? _acc;
    private CalcOp? _op;
    private string _entry = "0";            // what is typed or shown, with a dot as the decimal mark
    private bool _fresh = true;             // the next digit starts a new number
    private bool _done;                     // a result is on the display: a digit starts a new calculation
    private string _line = "";

    public CalcError Error { get; private set; }

    public bool HasError => Error != CalcError.None;

    /// <summary>The number on the display, with a decimal comma.</summary>
    public string Display => _entry.Replace('.', ',');

    /// <summary>The upper line: the first operand and the operator waiting, or the finished calculation.</summary>
    public string Expression => HasError ? "" : _op is { } op ? $"{Format(_acc ?? 0)} {Symbol(op)}" : _line;

    /// <summary>The value on the display.</summary>
    public decimal Value => decimal.Parse(_entry, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The value as text to paste elsewhere: a decimal comma, no grouping, no trailing zeros.</summary>
    public string ResultText => HasError ? "" : Format(Value);

    public static string Symbol(CalcOp op) => op switch { CalcOp.Add => "+", CalcOp.Subtract => "−", CalcOp.Multiply => "×", _ => "÷" };

    public static bool TryParseOp(char c, out CalcOp op)
    {
        switch (c)
        {
            case '+': op = CalcOp.Add; return true;
            case '-' or '−' or '–': op = CalcOp.Subtract; return true;
            case '*' or '×' or 'x' or 'X': op = CalcOp.Multiply; return true;
            case '/' or '÷' or ':': op = CalcOp.Divide; return true;
            default: op = default; return false;
        }
    }

    public void Digit(int digit)
    {
        if (digit is < 0 or > 9) return;
        StartIfFinished();
        if (_fresh)
        {
            _entry = digit.ToString(CultureInfo.InvariantCulture);
            _fresh = false;
        }
        else if (_entry == "0") _entry = digit.ToString(CultureInfo.InvariantCulture);
        else if (_entry == "-0") _entry = "-" + digit.ToString(CultureInfo.InvariantCulture);
        else if (_entry.Count(char.IsDigit) < MaxDigits) _entry += digit.ToString(CultureInfo.InvariantCulture);
    }

    public void Comma()
    {
        StartIfFinished();
        if (_fresh)
        {
            _entry = "0.";
            _fresh = false;
        }
        else if (!_entry.Contains('.')) _entry += ".";
    }

    public void Operator(CalcOp op)
    {
        if (HasError) return;
        if (_op is not null && _fresh)
        {
            _op = op;                      // another operator right after one: the later one wins
            return;
        }

        var value = Value;
        if (_op is { } pending)
        {
            if (!Apply(_acc ?? 0, pending, value, out var result)) return;
            value = result;
        }

        _acc = value;
        _op = op;
        _entry = Format(value).Replace(',', '.');
        _fresh = true;
        _done = false;
    }

    public void Equals()
    {
        if (HasError || _op is not { } op) return;
        var left = _acc ?? 0;
        var right = Value;
        if (!Apply(left, op, right, out var result)) return;

        _line = $"{Format(left)} {Symbol(op)} {Format(right)} =";
        _acc = null;
        _op = null;
        _entry = Format(result).Replace(',', '.');
        _fresh = true;
        _done = true;
    }

    /// <summary>The sign of the number on the display (a minus in front of a typed number).</summary>
    public void Negate()
    {
        if (HasError) return;
        if (_entry == "0" && _fresh) return;
        _entry = _entry.StartsWith('-') ? _entry[1..] : "-" + _entry;
    }

    public void Backspace()
    {
        if (HasError) { Clear(); return; }
        if (_fresh) return;                // a result or an operator's value is not edited digit by digit
        _entry = _entry[..^1];
        if (_entry is "" or "-") _entry = "0";
    }

    public void Clear()
    {
        _acc = null;
        _op = null;
        _entry = "0";
        _fresh = true;
        _done = false;
        _line = "";
        Error = CalcError.None;
    }

    private void StartIfFinished()
    {
        if (HasError || _done) Clear();
    }

    private bool Apply(decimal left, CalcOp op, decimal right, out decimal result)
    {
        result = 0;
        try
        {
            switch (op)
            {
                case CalcOp.Add: result = left + right; break;
                case CalcOp.Subtract: result = left - right; break;
                case CalcOp.Multiply: result = left * right; break;
                default:
                    if (right == 0) return Fail(CalcError.DivideByZero);
                    result = left / right;
                    break;
            }
        }
        catch (OverflowException)
        {
            return Fail(CalcError.Overflow);
        }

        result = Math.Round(result, ResultDecimals, MidpointRounding.AwayFromZero);
        return true;
    }

    private bool Fail(CalcError error)
    {
        Clear();
        Error = error;
        _entry = "0";
        return false;
    }

    private static string Format(decimal value) =>
        Math.Round(value, ResultDecimals, MidpointRounding.AwayFromZero).ToString("0.##########", CultureInfo.InvariantCulture).Replace('.', ',');
}
