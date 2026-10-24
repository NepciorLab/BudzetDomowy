using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

public static class RecurrenceEngine
{
    /// <summary>Safety limit for one catch-up run.</summary>
    public const int MaxOccurrencesPerRun = 1000;

    /// <summary>
    /// Dates the template falls on within [from, to], never before its start or after its end.
    /// Weekly: the ISO weekday in <see cref="RecurringTransaction.Day"/>. Monthly: that day of the month, shortened to the
    /// month's last day (31 in February is the 28th/29th). Yearly: the month of the start date, same day rule.
    /// </summary>
    public static IEnumerable<DateOnly> Occurrences(RecurringTransaction t, DateOnly from, DateOnly to)
    {
        var start = from < t.StartDate ? t.StartDate : from;
        var end = t.EndDate is { } e && e < to ? e : to;
        if (end < start) yield break;

        switch (t.Frequency)
        {
            case RecurrenceFrequency.Weekly:
            {
                var weekday = Math.Clamp(t.Day, 1, 7);
                var d = start;
                while (IsoWeekday(d) != weekday) d = d.AddDays(1);
                for (; d <= end; d = d.AddDays(7)) yield return d;
                break;
            }
            case RecurrenceFrequency.Monthly:
            {
                for (var m = new DateOnly(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
                {
                    var d = OnDay(m.Year, m.Month, t.Day);
                    if (d >= start && d <= end) yield return d;
                }
                break;
            }
            case RecurrenceFrequency.Yearly:
            {
                for (var y = start.Year; y <= end.Year; y++)
                {
                    var d = OnDay(y, t.StartDate.Month, t.Day);
                    if (d >= start && d <= end) yield return d;
                }
                break;
            }
        }
    }

    public static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static DateOnly OnDay(int year, int month, int day) =>
        new(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));
}

public sealed record RecurringRun(int Created, int Skipped, IReadOnlyList<Guid> TemplatesWaitingForRate);

public sealed class RecurringService
{
    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;
    private readonly TransactionService _transactions;
    private readonly ExchangeRateService _rates;

    public RecurringService(EventRecorder recorder, LocalReader reader, TransactionService transactions, ExchangeRateService rates)
    {
        _recorder = recorder;
        _reader = reader;
        _transactions = transactions;
        _rates = rates;
    }

    public RecurringTransaction Add(RecurringTransaction template)
    {
        var t = template with { Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id, Note = template.Note.Trim(), Place = template.Place.Trim() };
        _recorder.Save(t);
        return t;
    }

    public void Save(RecurringTransaction t) => _recorder.Save(t with { Note = t.Note.Trim(), Place = t.Place.Trim() });

    /// <summary>Deletes the template; transactions already generated from it stay.</summary>
    public void Delete(Guid id) => _recorder.Delete(EntityTypes.RecurringTransaction, id);

    /// <summary>
    /// Creates every transaction that is due up to <paramref name="today"/> and not created yet. The id of a generated
    /// transaction is derived from the template id and the date, so two computers produce the very same transaction
    /// instead of two copies, and running this again changes nothing. A generated transaction the user deleted is not recreated.
    /// </summary>
    public async Task<RecurringRun> GenerateDueAsync(DateOnly today, CancellationToken ct = default)
    {
        var created = 0;
        var skipped = 0;
        var waiting = new List<Guid>();

        foreach (var template in _reader.GetRecurringTransactions())
        {
            var from = template.LastGeneratedDate?.AddDays(1) ?? template.StartDate;
            DateOnly? lastHandled = null;
            var blocked = false;

            foreach (var date in RecurrenceEngine.Occurrences(template, from, today).Take(RecurrenceEngine.MaxOccurrencesPerRun))
            {
                ct.ThrowIfCancellationRequested();
                var id = DeterministicId.ForRecurrence(template.Id, date);
                if (_reader.GetTransaction(id) is not null || _reader.IsTombstone(EntityTypes.Transaction, id))
                {
                    skipped++;
                    lastHandled = date;
                    continue;
                }

                var rate = 1m;
                DateOnly? rateDate = null;
                if (CurrencyCatalog.IsForeign(template.CurrencyCode))
                {
                    var lookup = await _rates.GetRateAsync(template.CurrencyCode, date, ct).ConfigureAwait(false);
                    if (lookup.Rate is not { } r || lookup.Origin == RateOrigin.LastKnown)
                    {
                        // no trustworthy rate for that day: try again next time instead of booking a wrong amount
                        blocked = true;
                        break;
                    }
                    rate = r;
                    rateDate = lookup.EffectiveDate;
                }

                _transactions.Add(date, template.Type, template.Amount, template.CategoryId, template.Note, template.EventId,
                    place: template.Place, currency: template.CurrencyCode, rateToPln: rate, rateDate: rateDate,
                    id: id, createdAt: new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                created++;
                lastHandled = date;
            }

            if (blocked) waiting.Add(template.Id);
            if (lastHandled is { } last && (template.LastGeneratedDate is null || last > template.LastGeneratedDate))
                _recorder.Save(template with { LastGeneratedDate = last });
        }

        return new RecurringRun(created, skipped, waiting);
    }
}
