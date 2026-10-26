using System.Text.Json.Nodes;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using BudzetDomowy.Core.Storage;

namespace BudzetDomowy.Core.Sync;

/// <summary>
/// Write path for user changes: creates an event, appends it to this device's journal and applies it
/// to the local database. The journal is written first, so a crash never leaves the database ahead of it.
/// </summary>
public sealed class EventRecorder
{
    private readonly DeviceIdentity _device;
    private readonly HybridLogicalClock _clock;
    private readonly JournalWriter _writer;
    private readonly Materializer _materializer;
    private readonly SyncGuard? _guard;
    private readonly object _gate = new();

    public event EventHandler? Changed;

    public EventRecorder(DeviceIdentity device, HybridLogicalClock clock, JournalWriter writer, Materializer materializer, SyncGuard? guard = null)
    {
        _device = device;
        _clock = clock;
        _writer = writer;
        _materializer = materializer;
        _guard = guard;
    }

    public JournalEvent Upsert<T>(string entityType, Guid id, T entity) where T : notnull =>
        Record(entityType, id, EventOperation.Upsert, JournalJson.ToPayload(entity), entity);

    public JournalEvent Delete(string entityType, Guid id) => Record(entityType, id, EventOperation.Delete, null);

    public JournalEvent Save(Transaction t) => Upsert(EntityTypes.Transaction, t.Id, t);
    public JournalEvent Save(Category c) => Upsert(EntityTypes.Category, c.Id, c);
    public JournalEvent Save(SpecialEvent e) => Upsert(EntityTypes.SpecialEvent, e.Id, e);
    public JournalEvent Save(Budget b) => Upsert(EntityTypes.Budget, b.Id, b);
    public JournalEvent Save(RecurringTransaction r) => Upsert(EntityTypes.RecurringTransaction, r.Id, r);
    public JournalEvent Save(CategoryRule r) => Upsert(EntityTypes.CategoryRule, r.Id, r);
    public JournalEvent Save(Payee p) => Upsert(EntityTypes.Payee, p.Id, p);
    public JournalEvent Save(PlannedPayment p) => Upsert(EntityTypes.PlannedPayment, p.Id, p);
    public JournalEvent Save(PlaceHint h) => Upsert(EntityTypes.PlaceHint, h.Id, h);

    private JournalEvent Record(string entityType, Guid id, EventOperation op, JsonNode? payload, object? entity = null)
    {
        if (!EntityTypes.IsKnown(entityType)) throw new ArgumentException($"Unknown entity type '{entityType}'.", nameof(entityType));
        if (id == Guid.Empty) throw new ArgumentException("Entity id must not be empty.", nameof(id));
        _guard?.ThrowIfBlocked();

        JournalEvent ev;
        lock (_gate)
        {
            ev = new JournalEvent
            {
                EventId = Guid.NewGuid(),
                DeviceId = _device.DeviceId,
                Timestamp = _clock.Now(),
                EntityType = entityType,
                EntityId = id,
                Operation = op,
                Payload = payload,
                SchemaVersion = EntityTypes.SchemaOf(entityType, entity),
            };

            var (start, end) = _writer.Append(ev);
            var fileName = Path.GetFileName(_writer.Path);
            // Only move the stored offset when we were fully caught up, otherwise the next sync re-reads (idempotently).
            _materializer.Apply([ev], new FileProgress(fileName, _device.DeviceId, start, end, RequireStoredOffset: start));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return ev;
    }
}
