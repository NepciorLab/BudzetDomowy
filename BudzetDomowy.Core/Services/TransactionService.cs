using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

/// <summary>Creates, edits and deletes transactions. Every method returns what is needed to undo it.</summary>
public sealed class TransactionService
{
    private readonly EventRecorder _recorder;
    private readonly DeviceIdentity _device;
    private readonly LocalReader _reader;
    private readonly Func<DateTimeOffset> _now;

    public TransactionService(EventRecorder recorder, DeviceIdentity device, LocalReader reader, Func<DateTimeOffset>? now = null)
    {
        _recorder = recorder;
        _device = device;
        _reader = reader;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Adds a transaction with a single position. The PLN amount is computed and stored with the rate used.</summary>
    public Transaction Add(DateOnly date, TransactionType type, decimal amount, Guid? categoryId, string? note, Guid? eventId, string? place = null,
        string currency = "PLN", decimal rateToPln = 1m, DateOnly? rateDate = null, bool rateManual = false,
        Guid? id = null, DateTimeOffset? createdAt = null)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        if (rateToPln <= 0) throw new ArgumentOutOfRangeException(nameof(rateToPln), "Exchange rate must be positive.");
        currency = string.IsNullOrWhiteSpace(currency) ? "PLN" : currency.Trim().ToUpperInvariant();
        if (currency == "PLN") (rateToPln, rateDate, rateManual) = (1m, null, false);

        var t = new Transaction
        {
            Id = id ?? Guid.NewGuid(),
            Date = date,
            Type = type,
            Amount = amount,
            CurrencyCode = currency,
            ExchangeRateToPln = rateToPln,
            ExchangeRateDate = rateDate,
            ExchangeRateManual = rateManual,
            AmountPln = Money.ToPln(amount, rateToPln),
            CategoryId = categoryId,
            Place = (place ?? "").Trim(),
            Note = (note ?? "").Trim(),
            EventId = eventId,
            CreatedAt = createdAt ?? _now(),
            CreatedByDeviceId = _device.DeviceId,
            Lines = [new TransactionLine { Id = Guid.NewGuid(), CategoryId = categoryId, Amount = amount, Description = (note ?? "").Trim() }],
        };
        _recorder.Save(t);
        return t;
    }

    /// <summary>Saves an edited transaction; keeps the PLN amount and the single line consistent.</summary>
    public Transaction Update(Transaction edited)
    {
        if (edited.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(edited), "Amount must be positive.");

        var line = edited.Lines.Count == 1 ? edited.Lines[0] : null;
        var fixedUp = edited with
        {
            Place = edited.Place.Trim(),
            Note = edited.Note.Trim(),
            AmountPln = Money.ToPln(edited.Amount, edited.ExchangeRateToPln),
            Lines = line is null
                ? edited.Lines
                : [line with { Amount = edited.Amount, CategoryId = edited.CategoryId, Description = edited.Note.Trim() }],
        };
        _recorder.Save(fixedUp);
        return fixedUp;
    }

    public void Delete(IEnumerable<Transaction> transactions)
    {
        foreach (var t in transactions) _recorder.Delete(EntityTypes.Transaction, t.Id);
    }

    /// <summary>Writes the given versions again (undo of a delete or of an edit).</summary>
    public void Restore(IEnumerable<Transaction> versions)
    {
        foreach (var t in versions) _recorder.Save(t);
    }

    /// <summary>
    /// Gives many transactions the same amount (in each one's own currency; the amount in złoty follows its rate). Returns the previous
    /// versions for undo.
    /// </summary>
    public IReadOnlyList<Transaction> ChangeAmount(IEnumerable<Guid> ids, decimal amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        var wanted = ids.ToHashSet();
        var before = _reader.GetTransactions().Where(t => wanted.Contains(t.Id)).ToList();
        foreach (var t in before) Update(t with { Amount = amount });
        return before;
    }

    /// <summary>Changes the category of many transactions. Returns the previous versions for undo.</summary>
    public IReadOnlyList<Transaction> ChangeCategory(IEnumerable<Guid> ids, Guid? categoryId)
    {
        var wanted = ids.ToHashSet();
        var before = _reader.GetTransactions().Where(t => wanted.Contains(t.Id)).ToList();
        foreach (var t in before) Update(t with { CategoryId = categoryId });
        return before;
    }
}
