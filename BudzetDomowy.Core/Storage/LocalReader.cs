using System.Globalization;
using BudzetDomowy.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BudzetDomowy.Core.Storage;

public sealed record DeviceJournalInfo(string FileName, string DeviceId, long Offset, int EventCount, DateTimeOffset? LastReadUtc);

/// <summary>Read side of the local database: returns domain objects.</summary>
public sealed class LocalReader
{
    private readonly LocalDatabase _db;

    public LocalReader(LocalDatabase db) => _db = db;

    public IReadOnlyList<Transaction> GetTransactions(DateOnly? from = null, DateOnly? to = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (from is not null) { where.Add("date >= $from"); cmd.Parameters.AddWithValue("$from", Materializer.Day(from.Value)); }
        if (to is not null) { where.Add("date <= $to"); cmd.Parameters.AddWithValue("$to", Materializer.Day(to.Value)); }
        cmd.CommandText = "SELECT id, date, type, amount_cents, currency_code, exchange_rate, amount_pln_cents, category_id, note, event_id, created_at, created_by_device, place, rate_date, rate_manual FROM transactions"
                          + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                          + " ORDER BY date, created_at, id";

        var list = new List<Transaction>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new Transaction
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Date = ParseDay(r.GetString(1)),
                    Type = Enum.Parse<TransactionType>(r.GetString(2)),
                    Amount = Money.FromCents(r.GetInt64(3)),
                    CurrencyCode = r.GetString(4),
                    ExchangeRateToPln = decimal.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                    AmountPln = Money.FromCents(r.GetInt64(6)),
                    CategoryId = Guid_(r, 7),
                    Note = r.GetString(8),
                    EventId = Guid_(r, 9),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture),
                    CreatedByDeviceId = r.GetString(11),
                    Place = r.GetString(12),
                    ExchangeRateDate = r.IsDBNull(13) ? null : ParseDay(r.GetString(13)),
                    ExchangeRateManual = r.GetInt32(14) != 0,
                });
            }
        }

        var lines = GetLines(conn);
        return list.Select(t => lines.TryGetValue(t.Id, out var l) ? t with { Lines = l } : t).ToList();
    }

    public Transaction? GetTransaction(Guid id) => GetTransactions().FirstOrDefault(t => t.Id == id);

    private static Dictionary<Guid, List<TransactionLine>> GetLines(SqliteConnection conn)
    {
        var map = new Dictionary<Guid, List<TransactionLine>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, transaction_id, category_id, amount_cents, description FROM transaction_lines ORDER BY transaction_id, sort_order";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var tid = Guid.Parse(r.GetString(1));
            if (!map.TryGetValue(tid, out var l)) map[tid] = l = [];
            l.Add(new TransactionLine
            {
                Id = Guid.Parse(r.GetString(0)),
                CategoryId = Guid_(r, 2),
                Amount = Money.FromCents(r.GetInt64(3)),
                Description = r.GetString(4),
            });
        }
        return map;
    }

    public IReadOnlyList<Category> GetCategories() => Query("SELECT id, name, parent_id, type, color, icon, sort_order, is_archived FROM categories ORDER BY sort_order, name",
        r => new Category
        {
            Id = Guid.Parse(r.GetString(0)),
            Name = r.GetString(1),
            ParentId = Guid_(r, 2),
            Type = Enum.Parse<CategoryType>(r.GetString(3)),
            Color = r.GetString(4),
            Icon = r.GetString(5),
            SortOrder = r.GetInt32(6),
            IsArchived = r.GetInt32(7) != 0,
        });

    public IReadOnlyList<SpecialEvent> GetSpecialEvents() => Query("SELECT id, name, date_from, date_to, color, planned_budget_pln, note FROM special_events ORDER BY date_from, name",
        r => new SpecialEvent
        {
            Id = Guid.Parse(r.GetString(0)),
            Name = r.GetString(1),
            DateFrom = ParseDay(r.GetString(2)),
            DateTo = ParseDay(r.GetString(3)),
            Color = r.GetString(4),
            PlannedBudgetPln = r.IsDBNull(5) ? null : decimal.Parse(r.GetString(5), CultureInfo.InvariantCulture),
            Note = r.GetString(6),
        });

    public IReadOnlyList<Budget> GetBudgets() => Query("SELECT id, category_id, monthly_cents, valid_from_month FROM budgets ORDER BY category_id, valid_from_month",
        r => new Budget
        {
            Id = Guid.Parse(r.GetString(0)),
            CategoryId = Guid.Parse(r.GetString(1)),
            MonthlyAmount = Money.FromCents(r.GetInt64(2)),
            ValidFromMonth = ParseDay(r.GetString(3)),
        });

    public IReadOnlyList<RecurringTransaction> GetRecurringTransactions() => Query(
        "SELECT id, type, amount, currency_code, category_id, note, event_id, frequency, day, start_date, end_date, last_generated_date, place, confirm_each, skipped_dates, name FROM recurring_transactions ORDER BY start_date, id",
        r => new RecurringTransaction
        {
            Id = Guid.Parse(r.GetString(0)),
            Type = Enum.Parse<TransactionType>(r.GetString(1)),
            Amount = decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture),
            CurrencyCode = r.GetString(3),
            CategoryId = Guid_(r, 4),
            Note = r.GetString(5),
            EventId = Guid_(r, 6),
            Frequency = Enum.Parse<RecurrenceFrequency>(r.GetString(7)),
            Day = r.GetInt32(8),
            StartDate = ParseDay(r.GetString(9)),
            EndDate = r.IsDBNull(10) ? null : ParseDay(r.GetString(10)),
            LastGeneratedDate = r.IsDBNull(11) ? null : ParseDay(r.GetString(11)),
            Place = r.GetString(12),
            ConfirmEach = r.GetInt32(13) != 0,
            SkippedDates = r.GetString(14).Length == 0 ? [] : r.GetString(14).Split(',').Select(ParseDay).ToList(),
            Name = r.GetString(15),
        });

    public IReadOnlyList<CategoryRule> GetCategoryRules() => Query("SELECT id, pattern, category_id, event_id, use_count FROM category_rules ORDER BY pattern, id",
        r => new CategoryRule
        {
            Id = Guid.Parse(r.GetString(0)),
            Pattern = r.GetString(1),
            CategoryId = Guid.Parse(r.GetString(2)),
            EventId = Guid_(r, 3),
            UseCount = r.GetInt32(4),
        });

    /// <summary>Number of transactions that use the category directly or through one of their lines.</summary>
    public int CountTransactionsInCategory(Guid categoryId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM transactions
            WHERE category_id = $c OR id IN (SELECT transaction_id FROM transaction_lines WHERE category_id = $c)
            """;
        cmd.Parameters.AddWithValue("$c", Materializer.Id(categoryId));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>True if the entity was deleted (a tombstone exists).</summary>
    public bool IsTombstone(string entityType, Guid id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT deleted FROM entity_state WHERE entity_type = $t AND entity_id = $i";
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$i", Materializer.Id(id));
        var v = cmd.ExecuteScalar();
        return v is not null && Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
    }

    public IReadOnlyList<DeviceJournalInfo> GetJournalInfos() => Query(
        "SELECT file_name, device_id, byte_offset, event_count, last_read_utc FROM journal_offsets ORDER BY file_name",
        r => new DeviceJournalInfo(
            r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture)));

    // ----- local exchange rate cache -----

    public void SaveExchangeRates(IEnumerable<ExchangeRate> rates)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var rate in rates)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO exchange_rates (currency_code, date, rate, source, fetched_at) VALUES ($c, $d, $r, $s, $f)";
            cmd.Parameters.AddWithValue("$c", rate.CurrencyCode);
            cmd.Parameters.AddWithValue("$d", Materializer.Day(rate.Date));
            cmd.Parameters.AddWithValue("$r", Materializer.Dec(rate.Rate));
            cmd.Parameters.AddWithValue("$s", rate.Source.ToString());
            cmd.Parameters.AddWithValue("$f", rate.FetchedAt.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Latest cached NBP rate strictly before <paramref name="date"/>.</summary>
    public ExchangeRate? GetRateBefore(string currencyCode, DateOnly date) => Query(
        "SELECT currency_code, date, rate, source, fetched_at FROM exchange_rates WHERE currency_code = $c AND source = 'Nbp' AND date < $d ORDER BY date DESC LIMIT 1",
        MapRate, ("$c", currencyCode), ("$d", Materializer.Day(date))).FirstOrDefault();

    /// <summary>Most recent cached NBP rate of any date.</summary>
    public ExchangeRate? GetLatestRate(string currencyCode) => Query(
        "SELECT currency_code, date, rate, source, fetched_at FROM exchange_rates WHERE currency_code = $c AND source = 'Nbp' ORDER BY date DESC LIMIT 1",
        MapRate, ("$c", currencyCode)).FirstOrDefault();

    /// <summary>Remembers that every NBP publication day between the two dates is in the cache.</summary>
    public void AddRateCoverage(string currencyCode, DateOnly from, DateOnly to)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO exchange_rate_coverage (currency_code, from_date, to_date) VALUES ($c, $f, $t)";
        cmd.Parameters.AddWithValue("$c", currencyCode);
        cmd.Parameters.AddWithValue("$f", Materializer.Day(from));
        cmd.Parameters.AddWithValue("$t", Materializer.Day(to));
        cmd.ExecuteNonQuery();
    }

    public bool HasRateCoverage(string currencyCode, DateOnly from, DateOnly to)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM exchange_rate_coverage WHERE currency_code = $c AND from_date <= $f AND to_date >= $t LIMIT 1";
        cmd.Parameters.AddWithValue("$c", currencyCode);
        cmd.Parameters.AddWithValue("$f", Materializer.Day(from));
        cmd.Parameters.AddWithValue("$t", Materializer.Day(to));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Currencies of the most recently entered transactions, newest first.</summary>
    public IReadOnlyList<string> GetRecentCurrencies(int limit = 8) => Query(
        "SELECT currency_code FROM transactions GROUP BY currency_code ORDER BY MAX(created_at) DESC LIMIT $n",
        r => r.GetString(0), ("$n", limit));

    private static ExchangeRate MapRate(SqliteDataReader r) => new()
    {
        CurrencyCode = r.GetString(0),
        Date = ParseDay(r.GetString(1)),
        Rate = decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture),
        Source = Enum.Parse<ExchangeRateSource>(r.GetString(3)),
        FetchedAt = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
    };

    public void SaveExchangeRate(ExchangeRate rate)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO exchange_rates (currency_code, date, rate, source, fetched_at) VALUES ($c, $d, $r, $s, $f)";
        cmd.Parameters.AddWithValue("$c", rate.CurrencyCode);
        cmd.Parameters.AddWithValue("$d", Materializer.Day(rate.Date));
        cmd.Parameters.AddWithValue("$r", Materializer.Dec(rate.Rate));
        cmd.Parameters.AddWithValue("$s", rate.Source.ToString());
        cmd.Parameters.AddWithValue("$f", rate.FetchedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ExchangeRate> GetExchangeRates(string currencyCode) => Query(
        "SELECT currency_code, date, rate, source, fetched_at FROM exchange_rates WHERE currency_code = $c ORDER BY date DESC",
        r => new ExchangeRate
        {
            CurrencyCode = r.GetString(0),
            Date = ParseDay(r.GetString(1)),
            Rate = decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture),
            Source = Enum.Parse<ExchangeRateSource>(r.GetString(3)),
            FetchedAt = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
        },
        ("$c", currencyCode));

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] args)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static Guid? Guid_(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    private static DateOnly ParseDay(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
