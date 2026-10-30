using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BudzetDomowy.Tests;

public class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-mig-" + Guid.NewGuid().ToString("N"));
    public MigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void Exec(LocalDatabase db, string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ADatabaseCreatedByPhase2_IsUpgradedInPlace_WithItsDataKept()
    {
        var path = Path.Combine(_dir, "budzet.db");

        // build a database shaped like the one of the first released builds (schema v1: no place, no rate columns)
        var db = new LocalDatabase(path);
        var id = Guid.NewGuid().ToString("D");
        Exec(db, $"INSERT INTO transactions (id, date, type, amount_cents, currency_code, exchange_rate, amount_pln_cents, category_id, note, event_id, created_at, created_by_device, place) " +
                 $"VALUES ('{id}', '2026-10-01', 'Expense', 1250, 'PLN', '1', 1250, NULL, 'stara notatka', NULL, '2026-10-01T10:00:00+00:00', 'dev', '')");
        Exec(db, "ALTER TABLE transactions DROP COLUMN place");
        Exec(db, "ALTER TABLE transactions DROP COLUMN rate_date");
        Exec(db, "ALTER TABLE transactions DROP COLUMN rate_manual");
        Exec(db, "ALTER TABLE recurring_transactions DROP COLUMN place");
        Exec(db, "ALTER TABLE recurring_transactions DROP COLUMN confirm_each");
        Exec(db, "ALTER TABLE recurring_transactions DROP COLUMN skipped_dates");
        Exec(db, "ALTER TABLE recurring_transactions DROP COLUMN name");
        Exec(db, "DROP TABLE payees");
        Exec(db, "DROP TABLE planned_payments");
        Exec(db, "DROP TABLE place_hints");
        Exec(db, "ALTER TABLE budgets DROP COLUMN yearly_cents");
        Exec(db, "DROP TABLE exchange_rate_coverage");
        Exec(db, "PRAGMA user_version = 1");
        SqliteConnection.ClearAllPools();

        // opening it with the current code runs the missing migrations
        var upgraded = new LocalDatabase(path);
        using (var conn = upgraded.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal((long)LocalDatabase.CurrentVersion, (long)cmd.ExecuteScalar()!);
        }

        var t = Assert.Single(new LocalReader(upgraded).GetTransactions());
        Assert.Equal("stara notatka", t.Note);
        Assert.Equal("", t.Place);                    // new column, empty for old rows
        Assert.Null(t.ExchangeRateDate);
        Assert.False(t.ExchangeRateManual);
        Assert.Equal(12.50m, t.AmountPln);

        // and the upgraded database is fully usable: rate cache and new events
        var reader = new LocalReader(upgraded);
        reader.AddRateCoverage("EUR", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20));
        Assert.True(reader.HasRateCoverage("EUR", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void ADatabaseFromANewerProgram_IsRefusedInsteadOfBeingDamaged()
    {
        var path = Path.Combine(_dir, "future.db");
        var db = new LocalDatabase(path);
        Exec(db, $"PRAGMA user_version = {LocalDatabase.CurrentVersion + 1}");
        SqliteConnection.ClearAllPools();

        Assert.Throws<BudzetDomowy.Core.Storage.NewerDataException>(() => new LocalDatabase(path));   // a kind of InvalidOperationException the program can answer with an update
    }
}
