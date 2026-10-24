using System.Globalization;
using System.Text.RegularExpressions;

namespace BudzetDomowy.Core.Services;

/// <summary>Parses amounts typed by a user: "12,50", "12.50", "1 234,56", "1.234,56", "12,5 zł".</summary>
public static partial class AmountParser
{
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = CurrencySuffix().Replace(text.Trim(), "");
        s = s.Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return false;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // The later separator is the decimal one; the other is a thousands separator.
            var decimalSep = lastComma > lastDot ? ',' : '.';
            var thousandsSep = decimalSep == ',' ? '.' : ',';
            s = s.Replace(thousandsSep.ToString(), "").Replace(decimalSep, '.');
        }
        else if (lastComma >= 0)
        {
            if (s.Count(ch => ch == ',') > 1) return false;
            s = s.Replace(',', '.');
        }
        else if (lastDot >= 0 && s.Count(ch => ch == '.') > 1)
        {
            return false;
        }

        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
        if (value < 0) return false;
        amount = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"\s*(zł|zl|pln)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencySuffix();
}
