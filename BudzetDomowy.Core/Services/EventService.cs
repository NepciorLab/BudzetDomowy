using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

/// <summary>What a deleted event took with it, enough to put everything back.</summary>
public sealed record EventDeletion(SpecialEvent Event, IReadOnlyList<Transaction> DetachedTransactions);

public sealed class EventService
{
    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;
    private readonly TransactionService _transactions;

    public EventService(EventRecorder recorder, LocalReader reader, TransactionService transactions)
    {
        _recorder = recorder;
        _reader = reader;
        _transactions = transactions;
    }

    public SpecialEvent Add(string name, DateOnly from, DateOnly to, string color = "", decimal? plannedBudgetPln = null, string note = "")
    {
        var e = new SpecialEvent
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            DateFrom = from,
            DateTo = to < from ? from : to,
            Color = color,
            PlannedBudgetPln = plannedBudgetPln,
            Note = note,
        };
        _recorder.Save(e);
        return e;
    }

    public SpecialEvent Save(SpecialEvent e)
    {
        var fixedUp = e with { Name = e.Name.Trim(), DateTo = e.DateTo < e.DateFrom ? e.DateFrom : e.DateTo };
        _recorder.Save(fixedUp);
        return fixedUp;
    }

    /// <summary>Deletes the event and removes it from its transactions (the transactions themselves stay).</summary>
    public EventDeletion Delete(Guid id)
    {
        var e = _reader.GetSpecialEvents().First(x => x.Id == id);
        var detached = _reader.GetTransactions().Where(t => t.EventId == id).ToList();
        foreach (var t in detached) _transactions.Update(t with { EventId = null });
        _recorder.Delete(EntityTypes.SpecialEvent, id);
        return new EventDeletion(e, detached);
    }

    public void Restore(EventDeletion deletion)
    {
        _recorder.Save(deletion.Event);
        _transactions.Restore(deletion.DetachedTransactions);
    }
}
