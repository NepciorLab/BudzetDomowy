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

    /// <summary>Adds a PLN transaction with a single position (foreign currencies arrive in a later phase).</summary>
    public Transaction Add(DateOnly date, TransactionType type, decimal amount, Guid? categoryId, string? note, Guid? eventId, string? place = null)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        var id = Guid.NewGuid();
        var t = new Transaction
        {
            Id = id,
            Date = date,
            Type = type,
            Amount = amount,
            CurrencyCode = "PLN",
            ExchangeRateToPln = 1m,
            AmountPln = Money.ToPln(amount, 1m),
            CategoryId = categoryId,
            Place = (place ?? "").Trim(),
            Note = (note ?? "").Trim(),
            EventId = eventId,
            CreatedAt = _now(),
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

    /// <summary>Changes the category of many transactions. Returns the previous versions for undo.</summary>
    public IReadOnlyList<Transaction> ChangeCategory(IEnumerable<Guid> ids, Guid? categoryId)
    {
        var wanted = ids.ToHashSet();
        var before = _reader.GetTransactions().Where(t => wanted.Contains(t.Id)).ToList();
        foreach (var t in before) Update(t with { CategoryId = categoryId });
        return before;
    }
}
