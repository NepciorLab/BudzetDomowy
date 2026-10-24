namespace BudzetDomowy.Core.Currency;

public sealed record CurrencyOption(string Code, string Name)
{
    public string Display => Name.Length == 0 ? Code : $"{Code} · {Name}";
    public override string ToString() => Code;
}

/// <summary>Currencies of the NBP table A (the ones whose rates the program can fetch), plus PLN.</summary>
public static class CurrencyCatalog
{
    public const string Pln = "PLN";

    private static readonly (string Code, string Name)[] Known =
    [
        ("PLN", "złoty"), ("EUR", "euro"), ("USD", "dolar amerykański"), ("GBP", "funt szterling"),
        ("CHF", "frank szwajcarski"), ("CZK", "korona czeska"), ("NOK", "korona norweska"), ("SEK", "korona szwedzka"),
        ("DKK", "korona duńska"), ("HUF", "forint"), ("UAH", "hrywna"), ("CAD", "dolar kanadyjski"),
        ("AUD", "dolar australijski"), ("NZD", "dolar nowozelandzki"), ("JPY", "jen"), ("CNY", "juan"),
        ("TRY", "lira turecka"), ("RON", "lej rumuński"), ("BGN", "lew bułgarski"), ("ISK", "korona islandzka"),
        ("HKD", "dolar hongkoński"), ("SGD", "dolar singapurski"), ("THB", "bat tajlandzki"), ("ILS", "szekel"),
        ("KRW", "won południowokoreański"), ("INR", "rupia indyjska"), ("MXN", "peso meksykańskie"),
        ("BRL", "real brazylijski"), ("ZAR", "rand"), ("MYR", "ringgit"), ("IDR", "rupia indonezyjska"),
        ("PHP", "peso filipińskie"), ("CLP", "peso chilijskie"),
    ];

    public static IReadOnlySet<string> Codes { get; } = Known.Select(k => k.Code).ToHashSet();

    public static string NameOf(string code) =>
        Known.FirstOrDefault(k => k.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? "";

    public static bool IsForeign(string code) => !code.Equals(Pln, StringComparison.OrdinalIgnoreCase);

    /// <summary>PLN first, then the currencies used most recently (newest first), then everything else alphabetically.</summary>
    public static IReadOnlyList<CurrencyOption> Ordered(IEnumerable<string> recentCodes)
    {
        var result = new List<CurrencyOption> { new(Pln, NameOf(Pln)) };
        var seen = new HashSet<string> { Pln };
        foreach (var code in recentCodes)
        {
            var c = code.ToUpperInvariant();
            if (seen.Add(c)) result.Add(new CurrencyOption(c, NameOf(c)));
        }
        foreach (var (code, name) in Known.OrderBy(k => k.Code, StringComparer.Ordinal))
            if (seen.Add(code)) result.Add(new CurrencyOption(code, name));
        return result;
    }
}
