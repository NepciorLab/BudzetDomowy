using System.Globalization;

namespace BudzetDomowy.Core.Services;

/// <summary>
/// An amount typed as a sum: "2+2*2", "(10,50+4)/2", "12,5*3 EUR". Supports + - * / (also × ÷), brackets and a decimal comma or dot.
/// The result is rounded to cents and must not be negative. The single letter "r" is not an expression: it asks for the remainder.
/// </summary>
public static class AmountExpression
{
    /// <summary>"r" in an amount field: this line gets what is left of the total.</summary>
    public static bool IsRemainder(string? text) => text is not null && text.Trim().Equals("r", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text is just a number (optionally with a currency), not a calculation.</summary>
    public static bool IsPlainAmount(string? text, IReadOnlySet<string> codes) =>
        AmountParser.TryParseWithCurrency(text, codes, out _, out _);

    public static bool TryEvaluate(string? text, IReadOnlySet<string> codes, out decimal value, out string? currency)
    {
        value = 0;
        currency = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (AmountParser.TryParseWithCurrency(text, codes, out value, out currency)) return true;

        // a calculation, possibly followed by a currency: "2+2*2 EUR"
        var s = text.Trim();
        var last = s[^1];
        if (last is '€' or '$' or '£')
        {
            currency = last switch { '€' => "EUR", '$' => "USD", _ => "GBP" };
            s = s[..^1];
        }
        else if (s.Length > 3 && char.IsLetter(s[^1]) && char.IsLetter(s[^2]) && char.IsLetter(s[^3]) && codes.Contains(s[^3..].ToUpperInvariant()))
        {
            currency = s[^3..].ToUpperInvariant();
            s = s[..^3];
        }
        var ok = TryEvaluate(s, out value);
        if (!ok) currency = null;
        return ok;
    }

    public static bool TryEvaluate(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parser = new Parser(text.Replace(" ", "").Replace(" ", "").Replace('×', '*').Replace('÷', '/').Replace(':', '/'));
        try
        {
            var result = parser.ParseAll();
            if (result is null || result < 0) return false;
            value = Math.Round(result.Value, 2, MidpointRounding.AwayFromZero);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public decimal? ParseAll()
        {
            var value = Expression();
            return value is not null && _pos == text.Length ? value : null;
        }

        private decimal? Expression()
        {
            var left = Term();
            while (left is not null && _pos < text.Length && text[_pos] is '+' or '-')
            {
                var plus = text[_pos++] == '+';
                var right = Term();
                if (right is null) return null;
                left = plus ? left + right : left - right;
            }
            return left;
        }

        private decimal? Term()
        {
            var left = Factor();
            while (left is not null && _pos < text.Length && text[_pos] is '*' or '/')
            {
                var times = text[_pos++] == '*';
                var right = Factor();
                if (right is null) return null;
                if (!times && right == 0) return null;
                left = times ? left * right : left / right;
            }
            return left;
        }

        private decimal? Factor()
        {
            if (_pos >= text.Length) return null;
            var c = text[_pos];
            if (c == '-')
            {
                _pos++;
                return -Factor();
            }
            if (c == '+')
            {
                _pos++;
                return Factor();
            }
            if (c == '(')
            {
                _pos++;
                var inner = Expression();
                if (inner is null || _pos >= text.Length || text[_pos] != ')') return null;
                _pos++;
                return inner;
            }
            return Number();
        }

        private decimal? Number()
        {
            var start = _pos;
            var separators = 0;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] is ',' or '.'))
            {
                if (text[_pos] is ',' or '.') separators++;
                _pos++;
            }
            if (_pos == start || separators > 1) return null;
            var token = text[start.._pos].Replace(',', '.');
            return decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
    }
}
