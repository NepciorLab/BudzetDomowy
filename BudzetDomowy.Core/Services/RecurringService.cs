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
    /// Every N days: the start date and every N-th day after it.
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
            case RecurrenceFrequency.EveryDays:
            {
                var step = Math.Max(1, t.Day);
                var skip = start > t.StartDate ? (start.DayNumber - t.StartDate.DayNumber + step - 1) / step : 0;
                for (var d = t.StartDate.AddDays(skip * step); d <= end; d = d.AddDays(step)) yield return d;
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

/// <summary>An occurrence of a template that asks for confirmation and has not been answered yet.</summary>
public sealed record PendingOccurrence(RecurringTransaction Template, DateOnly Date);

public sealed record RecurringRun(int Created, int Skipped, IReadOnlyList<Guid> TemplatesWaitingForRate)
{
    /// <summary>Occurrences waiting for the user's "yes" or "no" (templates with confirmation switched on), oldest first.</summary>
    public IReadOnlyList<PendingOccurrence> Pending { get; init; } = [];
}

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
        var t = template with { Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id, Name = template.Name.Trim(), Note = template.Note.Trim(), Place = template.Place.Trim() };
        _recorder.Save(t);
        return t;
    }

    public void Save(RecurringTransaction t) => _recorder.Save(t with { Name = t.Name.Trim(), Note = t.Note.Trim(), Place = t.Place.Trim() });

    /// <summary>Deletes the template; transactions already generated from it stay.</summary>
    public void Delete(Guid id) => _recorder.Delete(EntityTypes.RecurringTransaction, id);

    /// <summary>
    /// Creates every transaction that is due up to <paramref name="today"/> and not created yet. The id of a generated
    /// transaction is derived from the template id and the date, so two computers produce the very same transaction
    /// instead of two copies, and running this again changes nothing. A generated transaction the user deleted is not recreated.
    /// Templates with <see cref="RecurringTransaction.ConfirmEach"/> create nothing here: their due occurrences come back in
    /// <see cref="RecurringRun.Pending"/> until <see cref="ConfirmAsync"/> or <see cref="Decline"/> answers them.
    /// </summary>
    public async Task<RecurringRun> GenerateDueAsync(DateOnly today, CancellationToken ct = default)
    {
        var created = 0;
        var skipped = 0;
        var waiting = new List<Guid>();
        var pending = new List<PendingOccurrence>();

        foreach (var template in _reader.GetRecurringTransactions())
        {
            var from = template.LastGeneratedDate?.AddDays(1) ?? template.StartDate;
            DateOnly? lastHandled = null;
            var blocked = false;
            var unanswered = false;   // the watermark must not pass an occurrence that still waits for an answer

            foreach (var date in RecurrenceEngine.Occurrences(template, from, today).Take(RecurrenceEngine.MaxOccurrencesPerRun))
            {
                ct.ThrowIfCancellationRequested();
                var id = DeterministicId.ForRecurrence(template.Id, date);
                if (_reader.GetTransaction(id) is not null || _reader.IsTombstone(EntityTypes.Transaction, id) || template.SkippedDates.Contains(date))
                {
                    skipped++;
                    if (!unanswered) lastHandled = date;
                    continue;
                }

                if (template.ConfirmEach)
                {
                    pending.Add(new PendingOccurrence(template, date));
                    unanswered = true;
                    continue;
                }

                if (!await CreateAsync(template, date, id, ct).ConfigureAwait(false))
                {
                    // no trustworthy rate for that day: try again next time instead of booking a wrong amount
                    blocked = true;
                    break;
                }
                created++;
                lastHandled = date;
            }

            if (blocked) waiting.Add(template.Id);
            if (lastHandled is { } last && (template.LastGeneratedDate is null || last > template.LastGeneratedDate))
                _recorder.Save(template with { LastGeneratedDate = last });
        }

        return new RecurringRun(created, skipped, waiting) { Pending = pending.OrderBy(p => p.Date).ToList() };
    }

    /// <summary>The user said "yes, it happened": books the occurrence. False when no trustworthy exchange rate was available yet.</summary>
    public async Task<bool> ConfirmAsync(Guid templateId, DateOnly date, CancellationToken ct = default)
    {
        var template = _reader.GetRecurringTransactions().FirstOrDefault(t => t.Id == templateId);
        if (template is null) return false;
        var id = DeterministicId.ForRecurrence(template.Id, date);
        if (_reader.GetTransaction(id) is not null) return true;
        return await CreateAsync(template, date, id, ct).ConfigureAwait(false);
    }

    /// <summary>The user said "no, it did not happen": the occurrence is never booked and never asked about again.</summary>
    public void Decline(Guid templateId, DateOnly date)
    {
        var template = _reader.GetRecurringTransactions().FirstOrDefault(t => t.Id == templateId);
        if (template is null || template.SkippedDates.Contains(date)) return;
        _recorder.Save(template with { SkippedDates = [.. template.SkippedDates, date] });
    }

    private async Task<bool> CreateAsync(RecurringTransaction template, DateOnly date, Guid id, CancellationToken ct)
    {
        var rate = 1m;
        DateOnly? rateDate = null;
        if (CurrencyCatalog.IsForeign(template.CurrencyCode))
        {
            var lookup = await _rates.GetRateAsync(template.CurrencyCode, date, ct).ConfigureAwait(false);
            if (lookup.Rate is not { } r || lookup.Origin == RateOrigin.LastKnown) return false;
            rate = r;
            rateDate = lookup.EffectiveDate;
        }

        _transactions.Add(date, template.Type, template.Amount, template.CategoryId, template.Note, template.EventId,
            place: template.Place, currency: template.CurrencyCode, rateToPln: rate, rateDate: rateDate,
            id: id, createdAt: new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        return true;
    }
}
