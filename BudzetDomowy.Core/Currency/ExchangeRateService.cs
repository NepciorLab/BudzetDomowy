using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Currency;

public enum RateOrigin
{
    /// <summary>PLN: the rate is 1.</summary>
    Pln,
    /// <summary>Fetched from NBP just now.</summary>
    Nbp,
    /// <summary>Taken from the local cache; the cache is known to hold the right NBP day.</summary>
    Cache,
    /// <summary>NBP was unreachable; the newest rate we have is offered (it may not be the right day).</summary>
    LastKnown,
    /// <summary>No rate available: the user has to type one.</summary>
    None,
}

public sealed record RateLookup(RateOrigin Origin, decimal? Rate, DateOnly? EffectiveDate, string? Error = null)
{
    public bool HasRate => Rate is not null;
    /// <summary>True when the rate should be confirmed by the user (offline fallback).</summary>
    public bool NeedsAttention => Origin is RateOrigin.LastKnown or RateOrigin.None;
}

/// <summary>
/// Finds the NBP table A rate for a transaction date: the rate of the last working day before that date
/// (weekends and holidays fall out naturally because NBP publishes only on working days). Results are cached
/// locally; when NBP cannot be reached the newest known rate is offered.
/// </summary>
public sealed class ExchangeRateService
{
    /// <summary>How far back to look for a publication day (covers long holiday gaps).</summary>
    private const int WindowDays = 14;
    private const int WideWindowDays = 45;

    private readonly LocalReader _reader;
    private readonly IExchangeRateSource _source;
    private readonly Func<DateOnly> _today;

    public ExchangeRateService(LocalReader reader, IExchangeRateSource source, Func<DateOnly>? today = null)
    {
        _reader = reader;
        _source = source;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    public async Task<RateLookup> GetRateAsync(string currencyCode, DateOnly transactionDate, CancellationToken ct = default)
    {
        var code = currencyCode.Trim().ToUpperInvariant();
        if (code == CurrencyCatalog.Pln) return new RateLookup(RateOrigin.Pln, 1m, null);

        var lastDay = transactionDate.AddDays(-1);

        var cached = _reader.GetRateBefore(code, transactionDate);
        if (cached is not null && IsCacheComplete(code, cached.Date, lastDay))
            return new RateLookup(RateOrigin.Cache, cached.Rate, cached.Date);

        try
        {
            var fetchedTo = Min(lastDay, _today());
            foreach (var window in new[] { WindowDays, WideWindowDays })
            {
                var from = fetchedTo.AddDays(-window);
                var rates = await _source.GetRatesAsync(code, from, fetchedTo, ct).ConfigureAwait(false);
                if (rates.Count == 0) continue;

                _reader.SaveExchangeRates(rates);
                _reader.AddRateCoverage(code, from, fetchedTo);
                var best = rates.Where(r => r.Date < transactionDate).OrderByDescending(r => r.Date).FirstOrDefault();
                if (best is not null) return new RateLookup(RateOrigin.Nbp, best.Rate, best.Date);
            }
            return Fallback(code, transactionDate, "Brak kursu NBP z tego okresu.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException
                                      or IOException or System.Text.Json.JsonException or KeyNotFoundException or FormatException)
        {
            return Fallback(code, transactionDate, e.Message);
        }
    }

    /// <summary>The cache holds the right day when it has the day just before the transaction, or was filled for a range reaching it.</summary>
    private bool IsCacheComplete(string code, DateOnly cachedDate, DateOnly lastDay)
    {
        if (cachedDate == lastDay) return true;
        var needed = Min(lastDay, _today().AddDays(-1));
        return needed >= cachedDate && _reader.HasRateCoverage(code, cachedDate, needed);
    }

    private RateLookup Fallback(string code, DateOnly transactionDate, string error)
    {
        var known = _reader.GetRateBefore(code, transactionDate) ?? _reader.GetLatestRate(code);
        return known is null
            ? new RateLookup(RateOrigin.None, null, null, error)
            : new RateLookup(RateOrigin.LastKnown, known.Rate, known.Date, error);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}
