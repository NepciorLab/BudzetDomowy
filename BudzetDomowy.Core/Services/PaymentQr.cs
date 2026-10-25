using System.Text.RegularExpressions;

namespace BudzetDomowy.Core.Services;

/// <param name="Truncated">The recipient or the title did not fit the fields of the standard and was shortened (or had characters it does not allow removed).</param>
/// <param name="AmountOmitted">The amount does not fit the six digits of the standard (over 9 999,99 zł): the code carries none and the amount is typed in the bank.</param>
public sealed record QrPayload(string Text, bool Truncated, bool AmountOmitted);

/// <summary>
/// The text of a payment QR code of Polish banks, as in the Polish Bank Association recommendation for the two-dimensional code
/// (standard 2D, version 1.0). Fields are separated by "|": recipient NIP, country (PL), account (26 digits), amount in grosze
/// (6 digits), recipient name (up to 20 characters), title (up to 32), customer id for direct debit (20), Invoobill id (12), reserved (24).
/// The code is a QR (error correction level L, UTF-8, at least 1.8 cm).
/// </summary>
public static partial class PaymentQr
{
    public const char Delimiter = '|';
    public const int RecipientLength = 20;
    public const int TitleLength = 32;
    public const long MaxGrosze = 999_999;

    // letters, digits and a few signs; everything else (also the delimiter itself) is dropped
    [GeneratedRegex(@"[^A-Za-z0-9 ,\.\/\\\-@#&\*ąćęłńóśźżĄĆĘŁŃÓŚŹŻ_]")]
    private static partial Regex Disallowed();

    public static string Clean(string? text) => Disallowed().Replace(text ?? "", "").Trim();

    public static QrPayload Build(string accountDigits, string recipient, string title, decimal amount, string? nip = null)
    {
        var grosze = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        var omitted = grosze is < 0 or > MaxGrosze;
        if (omitted) grosze = 0;

        var cleanRecipient = Clean(recipient);
        var cleanTitle = Clean(title);
        var truncated = cleanRecipient.Length > RecipientLength || cleanTitle.Length > TitleLength
                        || cleanRecipient != (recipient ?? "").Trim() || cleanTitle != (title ?? "").Trim();

        var nipDigits = new string((nip ?? "").Where(char.IsDigit).ToArray());
        var parts = new[]
        {
            nipDigits.Length == 10 ? nipDigits : "",
            "PL",
            accountDigits,
            grosze.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            Cut(cleanRecipient, RecipientLength),
            Cut(cleanTitle, TitleLength),
            "", "", "",
        };
        return new QrPayload(string.Join(Delimiter, parts), truncated, omitted);
    }

    private static string Cut(string text, int length) => (text.Length <= length ? text : text[..length]).Trim();
}
