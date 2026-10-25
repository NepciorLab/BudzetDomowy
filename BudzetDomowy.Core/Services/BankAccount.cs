using System.Text;

namespace BudzetDomowy.Core.Services;

/// <summary>A domestic Polish bank account number (NRB): 26 digits, the first two are a check number (mod 97, as in IBAN).</summary>
public static class BankAccount
{
    public const int Length = 26;

    /// <summary>
    /// The digits of what was typed: spaces, dashes and a leading "PL" are dropped. False when anything else is in it or when it
    /// is not 26 digits (the digits found are still returned, for showing what was understood).
    /// </summary>
    public static bool TryNormalize(string? input, out string digits)
    {
        digits = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();
        if (text.StartsWith("PL", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        var sb = new StringBuilder();
        var clean = true;
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9') sb.Append(ch);
            else if (ch is ' ' or '-' or '\u00A0' or '\u2009') continue;
            else clean = false;
        }
        digits = sb.ToString();
        return clean && digits.Length == Length;
    }

    /// <summary>The check number: the account moved as in IBAN (digits after the first two, "PL" = 2521, the check digits) must give 1 modulo 97.</summary>
    public static bool IsValid(string? digits)
    {
        if (digits is null || digits.Length != Length || digits.Any(c => c is < '0' or > '9')) return false;
        var rearranged = digits[2..] + "2521" + digits[..2];
        var remainder = 0;
        foreach (var ch in rearranged) remainder = (remainder * 10 + (ch - '0')) % 97;
        return remainder == 1;
    }

    /// <summary>Normalised and correct; the digits are returned for storing.</summary>
    public static bool TryParseValid(string? input, out string digits) => TryNormalize(input, out digits) && IsValid(digits);

    /// <summary>The standard groups: two check digits, then six groups of four ("61 1090 1014 0000 0712 1981 2874"). Also for a number typed so far.</summary>
    public static string Format(string? digits)
    {
        var d = new string((digits ?? "").Where(c => c is >= '0' and <= '9').ToArray());
        if (d.Length <= 2) return d;
        var sb = new StringBuilder(d[..2]);
        for (var i = 2; i < d.Length; i += 4)
        {
            sb.Append(' ');
            sb.Append(d.AsSpan(i, Math.Min(4, d.Length - i)));
        }
        return sb.ToString();
    }
}
