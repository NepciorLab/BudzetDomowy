using System.Globalization;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Journal;
using Microsoft.Data.Sqlite;

namespace BudzetDomowy.Core.Storage;

public sealed record ApplyResult(int Applied, int Ignored, int Failed)
{
    public static readonly ApplyResult Empty = new(0, 0, 0);

    public ApplyResult Add(ApplyResult o) => new(Applied + o.Applied, Ignored + o.Ignored, Failed + o.Failed);
}

/// <summary>Position reached in one journal file, saved atomically together with the events read from it.</summary>
/// <param name="RequireStoredOffset">
/// When set, the offset is only saved if the stored one equals this value (used for our own appends, so unread
/// events from earlier in the file are never skipped).
/// </param>
public sealed record FileProgress(string FileName, string DeviceId, long StartOffset, long NewOffset, long? RequireStoredOffset = null);

/// <summary>
/// Applies journal events to the local database. Last-writer-wins per entity, ordered by (HLC, DeviceId);
/// deletes leave a tombstone in entity_state. Applying the same event twice, or events in any order, gives the same result.
/// </summary>
public sealed class Materializer
{
    private readonly LocalDatabase _db;

    public Materializer(LocalDatabase db) => _db = db;

    public ApplyResult Apply(IReadOnlyList<JournalEvent> events, FileProgress? progress = null)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        int applied = 0, ignored = 0, failed = 0;

        foreach (var ev in events)
        {
            switch (ApplyOne(conn, tx, ev))
            {
                case Outcome.Applied: applied++; break;
                case Outcome.Ignored: ignored++; break;
                default: failed++; break;
            }
        }

        if (progress is not null && OffsetMayAdvance(conn, tx, progress))
        {
            var last = events.Count > 0 ? events.Max(e => e.Timestamp) : (HlcTimestamp?)null;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO journal_offsets (file_name, device_id, byte_offset, event_count, last_event_hlc, last_read_utc)
                VALUES ($f, $d, $o, $n, $h, $t)
                ON CONFLICT(file_name) DO UPDATE SET
                    device_id = $d,
                    byte_offset = $o,
                    event_count = CASE WHEN $start = 0 THEN $n ELSE event_count + $n END,
                    last_event_hlc = COALESCE($h, last_event_hlc),
                    last_read_utc = $t
                """;
            cmd.Parameters.AddWithValue("$f", progress.FileName);
            cmd.Parameters.AddWithValue("$d", progress.DeviceId);
            cmd.Parameters.AddWithValue("$o", progress.NewOffset);
            cmd.Parameters.AddWithValue("$n", events.Count);
            cmd.Parameters.AddWithValue("$start", progress.StartOffset);
            cmd.Parameters.AddWithValue("$h", last?.ToString() ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        if (applied > 0 || ignored > 0 || failed > 0) _db.Touch();
        return new ApplyResult(applied, ignored, failed);
    }

    private static bool OffsetMayAdvance(SqliteConnection conn, SqliteTransaction tx, FileProgress progress)
    {
        if (progress.RequireStoredOffset is not { } expected) return true;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT byte_offset FROM journal_offsets WHERE file_name = $f";
        cmd.Parameters.AddWithValue("$f", progress.FileName);
        var v = cmd.ExecuteScalar();
        var stored = v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return stored == expected;
    }

    public long GetOffset(string fileName)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT byte_offset FROM journal_offsets WHERE file_name = $f";
        cmd.Parameters.AddWithValue("$f", fileName);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    public HlcTimestamp MaxHlc()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(hlc) FROM entity_state";
        var v = cmd.ExecuteScalar();
        return v is string s && HlcTimestamp.TryParse(s, out var t) ? t : HlcTimestamp.Zero;
    }

    private enum Outcome { Applied, Ignored, Failed }

    private static Outcome ApplyOne(SqliteConnection conn, SqliteTransaction tx, JournalEvent ev)
    {
        if (!EntityTypes.IsKnown(ev.EntityType)) return Outcome.Failed;

        // Parse the payload first so a broken event never changes entity_state.
        object? entity = null;
        if (ev.Operation == EventOperation.Upsert)
        {
            try { entity = ParsePayload(ev); }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return Outcome.Failed; }
            if (entity is null) return Outcome.Failed;
        }

        var existing = ReadState(conn, tx, ev.EntityType, ev.EntityId);
        if (existing is { } st && !IsNewer(ev, st.Hlc, st.DeviceId)) return Outcome.Ignored;

        if (ev.Operation == EventOperation.Upsert) Project(conn, tx, ev.EntityType, entity!);
        else RemoveProjection(conn, tx, ev.EntityType, ev.EntityId);

        WriteState(conn, tx, ev);
        return Outcome.Applied;
    }

    private static bool IsNewer(JournalEvent ev, HlcTimestamp currentHlc, string currentDevice)
    {
        var c = ev.Timestamp.CompareTo(currentHlc);
        if (c != 0) return c > 0;
        return string.CompareOrdinal(ev.DeviceId, currentDevice) > 0;
    }

    private static (HlcTimestamp Hlc, string DeviceId)? ReadState(SqliteConnection conn, SqliteTransaction tx, string type, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT hlc, device_id FROM entity_state WHERE entity_type = $t AND entity_id = $i";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$i", Id(id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (HlcTimestamp.Parse(r.GetString(0)), r.GetString(1));
    }

    private static void WriteState(SqliteConnection conn, SqliteTransaction tx, JournalEvent ev)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entity_state (entity_type, entity_id, hlc, device_id, deleted, last_event_id)
            VALUES ($t, $i, $h, $d, $del, $e)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                hlc = $h, device_id = $d, deleted = $del, last_event_id = $e
            """;
        cmd.Parameters.AddWithValue("$t", ev.EntityType);
        cmd.Parameters.AddWithValue("$i", Id(ev.EntityId));
        cmd.Parameters.AddWithValue("$h", ev.Timestamp.ToString());
        cmd.Parameters.AddWithValue("$d", ev.DeviceId);
        cmd.Parameters.AddWithValue("$del", ev.Operation == EventOperation.Delete ? 1 : 0);
        cmd.Parameters.AddWithValue("$e", Id(ev.EventId));
        cmd.ExecuteNonQuery();
    }

    private static object? ParsePayload(JournalEvent ev)
    {
        object? entity = ev.EntityType switch
        {
            EntityTypes.Transaction => JournalJson.FromPayload<Transaction>(ev.Payload),
            EntityTypes.Category => JournalJson.FromPayload<Category>(ev.Payload),
            EntityTypes.SpecialEvent => JournalJson.FromPayload<SpecialEvent>(ev.Payload),
            EntityTypes.Budget => JournalJson.FromPayload<Budget>(ev.Payload),
            EntityTypes.RecurringTransaction => JournalJson.FromPayload<RecurringTransaction>(ev.Payload),
            EntityTypes.CategoryRule => JournalJson.FromPayload<CategoryRule>(ev.Payload),
            EntityTypes.Payee => JournalJson.FromPayload<Payee>(ev.Payload),
            EntityTypes.PlannedPayment => JournalJson.FromPayload<PlannedPayment>(ev.Payload),
            EntityTypes.PlaceHint => JournalJson.FromPayload<PlaceHint>(ev.Payload),
            _ => null,
        };
        // The envelope's EntityId is authoritative; a payload with another id is inconsistent.
        return entity is not null && PayloadId(entity) == ev.EntityId ? entity : null;
    }

    private static Guid PayloadId(object entity) => entity switch
    {
        Transaction t => t.Id,
        Category c => c.Id,
        SpecialEvent s => s.Id,
        Budget b => b.Id,
        RecurringTransaction r => r.Id,
        CategoryRule r => r.Id,
        Payee p => p.Id,
        PlannedPayment p => p.Id,
        PlaceHint h => h.Id,
        _ => Guid.Empty,
    };

    private static void Project(SqliteConnection conn, SqliteTransaction tx, string type, object entity)
    {
        switch (entity)
        {
            case Transaction t: ProjectTransaction(conn, tx, t); break;
            case Category c:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO categories (id, name, parent_id, type, color, icon, sort_order, is_archived)
                    VALUES ($id, $name, $parent, $type, $color, $icon, $sort, $arch)
                    """,
                    ("$id", Id(c.Id)), ("$name", c.Name), ("$parent", Id(c.ParentId)), ("$type", c.Type.ToString()),
                    ("$color", c.Color), ("$icon", c.Icon), ("$sort", c.SortOrder), ("$arch", c.IsArchived ? 1 : 0));
                break;
            case SpecialEvent s:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO special_events (id, name, date_from, date_to, color, planned_budget_pln, note)
                    VALUES ($id, $name, $from, $to, $color, $plan, $note)
                    """,
                    ("$id", Id(s.Id)), ("$name", s.Name), ("$from", Day(s.DateFrom)), ("$to", Day(s.DateTo)),
                    ("$color", s.Color), ("$plan", Dec(s.PlannedBudgetPln)), ("$note", s.Note));
                break;
            case Budget b:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO budgets (id, category_id, monthly_cents, valid_from_month)
                    VALUES ($id, $cat, $amt, $from)
                    """,
                    ("$id", Id(b.Id)), ("$cat", Id(b.CategoryId)), ("$amt", Money.ToCents(b.MonthlyAmount)),
                    ("$from", Day(b.ValidFromMonth)));
                break;
            case RecurringTransaction r:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO recurring_transactions
                        (id, type, amount, currency_code, category_id, place, note, event_id, frequency, day, start_date, end_date, last_generated_date, confirm_each, skipped_dates, name)
                    VALUES ($id, $type, $amt, $cur, $cat, $place, $note, $ev, $freq, $day, $start, $end, $last, $confirm, $skipped, $name)
                    """,
                    ("$id", Id(r.Id)), ("$type", r.Type.ToString()), ("$amt", Dec(r.Amount)), ("$cur", r.CurrencyCode),
                    ("$cat", Id(r.CategoryId)), ("$place", r.Place), ("$note", r.Note), ("$ev", Id(r.EventId)), ("$freq", r.Frequency.ToString()),
                    ("$day", r.Day), ("$start", Day(r.StartDate)), ("$end", Day(r.EndDate)), ("$last", Day(r.LastGeneratedDate)),
                    ("$name", r.Name), ("$confirm", r.ConfirmEach ? 1 : 0), ("$skipped", string.Join(",", r.SkippedDates.OrderBy(d => d).Select(d => Day(d)))));
                break;
            case CategoryRule k:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO category_rules (id, pattern, category_id, event_id, use_count)
                    VALUES ($id, $pat, $cat, $ev, $cnt)
                    """,
                    ("$id", Id(k.Id)), ("$pat", k.Pattern), ("$cat", Id(k.CategoryId)), ("$ev", Id(k.EventId)),
                    ("$cnt", k.UseCount));
                break;
            case Payee p:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO payees (id, name, account, category_id)
                    VALUES ($id, $name, $acc, $cat)
                    """,
                    ("$id", Id(p.Id)), ("$name", p.Name), ("$acc", p.AccountNumber), ("$cat", Id(p.CategoryId)));
                break;
            case PlannedPayment p:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO planned_payments (id, payee_id, due_date, title, amount_cents, status, series_id)
                    VALUES ($id, $payee, $due, $title, $amt, $status, $series)
                    """,
                    ("$id", Id(p.Id)), ("$payee", Id(p.PayeeId)), ("$due", Day(p.DueDate)), ("$title", p.Title),
                    ("$amt", Money.ToCents(p.Amount)), ("$status", p.Status.ToString()), ("$series", Id(p.SeriesId)));
                break;
            case PlaceHint h:
                Exec(conn, tx, """
                    INSERT OR REPLACE INTO place_hints (id, name, weight, hidden)
                    VALUES ($id, $name, $weight, $hidden)
                    """,
                    ("$id", Id(h.Id)), ("$name", h.Name), ("$weight", h.Weight is { } w ? w : DBNull.Value), ("$hidden", h.Hidden ? 1 : 0));
                break;
            default:
                throw new InvalidOperationException($"No projection for {type}.");
        }
    }

    private static void ProjectTransaction(SqliteConnection conn, SqliteTransaction tx, Transaction t)
    {
        Exec(conn, tx, """
            INSERT OR REPLACE INTO transactions
                (id, date, type, amount_cents, currency_code, exchange_rate, amount_pln_cents, category_id, place, note, event_id, created_at, created_by_device, rate_date, rate_manual)
            VALUES ($id, $date, $type, $amt, $cur, $rate, $pln, $cat, $place, $note, $ev, $created, $dev, $rdate, $rman)
            """,
            ("$id", Id(t.Id)), ("$date", Day(t.Date)), ("$type", t.Type.ToString()), ("$amt", Money.ToCents(t.Amount)),
            ("$cur", t.CurrencyCode), ("$rate", Dec(t.ExchangeRateToPln)), ("$pln", Money.ToCents(t.AmountPln)),
            ("$cat", Id(t.CategoryId)), ("$place", t.Place), ("$note", t.Note), ("$ev", Id(t.EventId)),
            ("$created", t.CreatedAt.ToString("o", CultureInfo.InvariantCulture)), ("$dev", t.CreatedByDeviceId),
            ("$rdate", Day(t.ExchangeRateDate)), ("$rman", t.ExchangeRateManual ? 1 : 0));

        Exec(conn, tx, "DELETE FROM transaction_lines WHERE transaction_id = $id", ("$id", Id(t.Id)));
        var order = 0;
        foreach (var line in t.Lines)
        {
            Exec(conn, tx, """
                INSERT OR REPLACE INTO transaction_lines (id, transaction_id, category_id, amount_cents, description, sort_order)
                VALUES ($id, $tid, $cat, $amt, $desc, $ord)
                """,
                ("$id", Id(line.Id)), ("$tid", Id(t.Id)), ("$cat", Id(line.CategoryId)),
                ("$amt", Money.ToCents(line.Amount)), ("$desc", line.Description), ("$ord", order++));
        }
    }

    private static void RemoveProjection(SqliteConnection conn, SqliteTransaction tx, string type, Guid id)
    {
        var table = type switch
        {
            EntityTypes.Transaction => "transactions",
            EntityTypes.Category => "categories",
            EntityTypes.SpecialEvent => "special_events",
            EntityTypes.Budget => "budgets",
            EntityTypes.RecurringTransaction => "recurring_transactions",
            EntityTypes.CategoryRule => "category_rules",
            EntityTypes.Payee => "payees",
            EntityTypes.PlannedPayment => "planned_payments",
            EntityTypes.PlaceHint => "place_hints",
            _ => throw new InvalidOperationException($"No projection for {type}."),
        };
        Exec(conn, tx, $"DELETE FROM {table} WHERE id = $id", ("$id", Id(id)));
        if (type == EntityTypes.Transaction)
            Exec(conn, tx, "DELETE FROM transaction_lines WHERE transaction_id = $id", ("$id", Id(id)));
    }

    internal static string Id(Guid id) => id.ToString("D");
    internal static object Id(Guid? id) => id is null ? DBNull.Value : id.Value.ToString("D");
    internal static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static object Day(DateOnly? d) => d is null ? DBNull.Value : Day(d.Value);
    internal static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    internal static object Dec(decimal? d) => d is null ? DBNull.Value : Dec(d.Value);

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
