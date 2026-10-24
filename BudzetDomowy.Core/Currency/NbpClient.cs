using System.Globalization;
using System.Net;
using System.Text.Json;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.Core.Currency;

/// <summary>Where exchange rates come from. Tests use a fake; the program uses <see cref="NbpClient"/>.</summary>
public interface IExchangeRateSource
{
    /// <summary>Rates published between the two dates (inclusive); an empty list when there were none.</summary>
    Task<IReadOnlyList<ExchangeRate>> GetRatesAsync(string currencyCode, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Public NBP API, table A (average rates): https://api.nbp.pl</summary>
public sealed class NbpClient : IExchangeRateSource
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<DateTimeOffset> _now;

    public NbpClient(HttpClient? http = null, string baseUrl = "https://api.nbp.pl/api/", Func<DateTimeOffset>? now = null)
    {
        _http = http ?? SharedClient;
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ExchangeRate>> GetRatesAsync(string currencyCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var code = currencyCode.ToUpperInvariant();
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{_baseUrl}exchangerates/rates/a/{code.ToLowerInvariant()}/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}/?format=json");

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return []; // no publication in this range
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var fetched = _now();
        var list = new List<ExchangeRate>();
        foreach (var item in doc.RootElement.GetProperty("rates").EnumerateArray())
        {
            list.Add(new ExchangeRate
            {
                CurrencyCode = code,
                Date = DateOnly.ParseExact(item.GetProperty("effectiveDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Rate = item.GetProperty("mid").GetDecimal(),
                Source = ExchangeRateSource.Nbp,
                FetchedAt = fetched,
            });
        }
        return list;
    }
}
