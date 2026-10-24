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

    /// <summary>Parses an exchange rate (more decimals than an amount): "4,2512" or "4.2512".</summary>
    public static bool TryParseRate(string? text, out decimal rate)
    {
        rate = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate) && rate > 0 && rate < 1_000_000m;
    }

    private static readonly Dictionary<char, string> Symbols = new() { ['€'] = "EUR", ['$'] = "USD", ['£'] = "GBP" };

    /// <summary>
    /// Like <see cref="TryParse"/> but also recognises a trailing currency: "12,50 EUR", "12.5eur", "9 €".
    /// <paramref name="currency"/> is null when none was typed (the caller keeps its selected currency).
    /// </summary>
    public static bool TryParseWithCurrency(string? text, IReadOnlySet<string> knownCodes, out decimal amount, out string? currency)
    {
        amount = 0;
        currency = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (Symbols.TryGetValue(s[^1], out var symbolCode))
        {
            currency = symbolCode;
            s = s[..^1];
        }
        else
        {
            var m = TrailingCode().Match(s);
            if (m.Success && knownCodes.Contains(m.Groups["code"].Value.ToUpperInvariant()))
            {
                var code = m.Groups["code"].Value.ToUpperInvariant();
                currency = code;
                s = s[..m.Index];
            }
        }

        if (!TryParse(s, out amount))
        {
            currency = null;
            return false;
        }
        return true;
    }

    [GeneratedRegex(@"\s*(?<code>[A-Za-z]{3})\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingCode();

    [GeneratedRegex(@"\s*(zł|zl|pln)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencySuffix();
}
