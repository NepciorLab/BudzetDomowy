using Microsoft.Data.Sqlite;

namespace BudzetDomowy.Core.Storage;

/// <summary>
/// Local SQLite database: a materialised view of all journals plus a few purely local tables
/// (journal offsets, exchange rate cache). Must live outside OneDrive.
/// </summary>
/// <summary>The data was written by a newer version of the program: this one must be updated before it can work with it.</summary>
public sealed class NewerDataException(string message) : InvalidOperationException(message);

public sealed class LocalDatabase
{
    /// <summary>Each entry upgrades the schema by one version (PRAGMA user_version).</summary>
    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE entity_state (
            entity_type   TEXT NOT NULL,
            entity_id     TEXT NOT NULL,
            hlc           TEXT NOT NULL,
            device_id     TEXT NOT NULL,
            deleted       INTEGER NOT NULL,
            last_event_id TEXT NOT NULL,
            PRIMARY KEY (entity_type, entity_id)
        );

        CREATE TABLE journal_offsets (
            file_name   TEXT PRIMARY KEY,
            device_id   TEXT NOT NULL,
            byte_offset INTEGER NOT NULL,
            event_count INTEGER NOT NULL DEFAULT 0,
            last_event_hlc TEXT,
            last_read_utc  TEXT
        );

        CREATE TABLE transactions (
            id                 TEXT PRIMARY KEY,
            date               TEXT NOT NULL,
            type               TEXT NOT NULL,
            amount_cents       INTEGER NOT NULL,
            currency_code      TEXT NOT NULL,
            exchange_rate      TEXT NOT NULL,
            amount_pln_cents   INTEGER NOT NULL,
            category_id        TEXT,
            note               TEXT NOT NULL,
            event_id           TEXT,
            created_at         TEXT NOT NULL,
            created_by_device  TEXT NOT NULL
        );
        CREATE INDEX ix_transactions_date ON transactions(date);
        CREATE INDEX ix_transactions_category ON transactions(category_id);
        CREATE INDEX ix_transactions_event ON transactions(event_id);

        CREATE TABLE transaction_lines (
            id             TEXT PRIMARY KEY,
            transaction_id TEXT NOT NULL,
            category_id    TEXT,
            amount_cents   INTEGER NOT NULL,
            description    TEXT NOT NULL,
            sort_order     INTEGER NOT NULL
        );
        CREATE INDEX ix_lines_transaction ON transaction_lines(transaction_id);
        CREATE INDEX ix_lines_category ON transaction_lines(category_id);

        CREATE TABLE categories (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            parent_id   TEXT,
            type        TEXT NOT NULL,
            color       TEXT NOT NULL,
            icon        TEXT NOT NULL,
            sort_order  INTEGER NOT NULL,
            is_archived INTEGER NOT NULL
        );

        CREATE TABLE special_events (
            id                 TEXT PRIMARY KEY,
            name               TEXT NOT NULL,
            date_from          TEXT NOT NULL,
            date_to            TEXT NOT NULL,
            color              TEXT NOT NULL,
            planned_budget_pln TEXT,
            note               TEXT NOT NULL
        );

        CREATE TABLE budgets (
            id               TEXT PRIMARY KEY,
            category_id      TEXT NOT NULL,
            monthly_cents    INTEGER NOT NULL,
            valid_from_month TEXT NOT NULL
        );
        CREATE INDEX ix_budgets_category ON budgets(category_id, valid_from_month);

        CREATE TABLE recurring_transactions (
            id                  TEXT PRIMARY KEY,
            type                TEXT NOT NULL,
            amount              TEXT NOT NULL,
            currency_code       TEXT NOT NULL,
            category_id         TEXT,
            note                TEXT NOT NULL,
            event_id            TEXT,
            frequency           TEXT NOT NULL,
            day                 INTEGER NOT NULL,
            start_date          TEXT NOT NULL,
            end_date            TEXT,
            last_generated_date TEXT
        );

        CREATE TABLE category_rules (
            id          TEXT PRIMARY KEY,
            pattern     TEXT NOT NULL,
            category_id TEXT NOT NULL,
            event_id    TEXT,
            use_count   INTEGER NOT NULL
        );
        CREATE INDEX ix_rules_pattern ON category_rules(pattern);

        CREATE TABLE exchange_rates (
            currency_code TEXT NOT NULL,
            date          TEXT NOT NULL,
            rate          TEXT NOT NULL,
            source        TEXT NOT NULL,
            fetched_at    TEXT NOT NULL,
            PRIMARY KEY (currency_code, date, source)
        );
        """,
        // v2: "place" next to the note (where the money was spent)
        """
        ALTER TABLE transactions ADD COLUMN place TEXT NOT NULL DEFAULT '';
        ALTER TABLE recurring_transactions ADD COLUMN place TEXT NOT NULL DEFAULT '';
        """,
        // v3: which NBP day a rate came from, manual rates, and which date ranges of the rate cache are complete
        """
        ALTER TABLE transactions ADD COLUMN rate_date TEXT;
        ALTER TABLE transactions ADD COLUMN rate_manual INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE exchange_rate_coverage (
            currency_code TEXT NOT NULL,
            from_date     TEXT NOT NULL,
            to_date       TEXT NOT NULL,
            PRIMARY KEY (currency_code, from_date, to_date)
        );
        """,
        // v4: recurring entries that ask for confirmation, and the occurrences answered "no"
        """
        ALTER TABLE recurring_transactions ADD COLUMN confirm_each INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE recurring_transactions ADD COLUMN skipped_dates TEXT NOT NULL DEFAULT '';
        """,
        // v5: a name for each recurring entry
        """
        ALTER TABLE recurring_transactions ADD COLUMN name TEXT NOT NULL DEFAULT '';
        """,
        // v6: payees and planned payments
        """
        CREATE TABLE payees (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            account     TEXT NOT NULL,
            category_id TEXT
        );
        CREATE TABLE planned_payments (
            id           TEXT PRIMARY KEY,
            payee_id     TEXT NOT NULL,
            due_date     TEXT NOT NULL,
            title        TEXT NOT NULL,
            amount_cents INTEGER NOT NULL,
            status       TEXT NOT NULL
        );
        CREATE INDEX ix_planned_payments_due ON planned_payments(due_date);
        """,
        // v7: payments typed together form a series that is edited as a whole
        """
        ALTER TABLE planned_payments ADD COLUMN series_id TEXT;
        """,
        // v8: corrections of the suggested places
        """
        CREATE TABLE place_hints (
            id     TEXT PRIMARY KEY,
            name   TEXT NOT NULL,
            weight INTEGER,
            hidden INTEGER NOT NULL DEFAULT 0
        );
        """,
    ];

    public static int CurrentVersion => Migrations.Length;

    public string Path { get; }

    private long _version;

    /// <summary>Grows whenever the data derived from the journals was changed (readers keep what they read until then).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Tells that the materialised data changed; called after the change is committed.</summary>
    internal void Touch() => Interlocked.Increment(ref _version);

    public LocalDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Migrate();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Open();
        var version = Scalar<long>(conn, "PRAGMA user_version");
        if (version > Migrations.Length)
            throw new NewerDataException(
                $"Local database has schema v{version}, newer than this app supports (v{Migrations.Length}).");

        for (var v = (int)version; v < Migrations.Length; v++)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version = {v + 1}";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Removes everything derived from journals (keeps the local exchange rate cache).</summary>
    public void ClearMaterializedData()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[]
                 {
                     "entity_state", "journal_offsets", "transactions", "transaction_lines", "categories",
                     "special_events", "budgets", "recurring_transactions", "category_rules", "payees", "planned_payments", "place_hints",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table}";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Touch();
    }

    internal static T Scalar<T>(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
