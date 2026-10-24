namespace BudzetDomowy.Core.Domain;

public enum TransactionType { Expense, Income }

public enum CategoryType { Expense, Income, Both }

public enum RecurrenceFrequency { Weekly, Monthly, Yearly }

/// <summary>Names of synchronised entity kinds as they appear in journal events.</summary>
public static class EntityTypes
{
    public const string Transaction = "Transaction";
    public const string Category = "Category";
    public const string SpecialEvent = "SpecialEvent";
    public const string Budget = "Budget";
    public const string RecurringTransaction = "RecurringTransaction";
    public const string CategoryRule = "CategoryRule";

    public static readonly IReadOnlyList<string> All =
    [
        Transaction, Category, SpecialEvent, Budget, RecurringTransaction, CategoryRule,
    ];

    public static bool IsKnown(string entityType) => All.Contains(entityType);
}

/// <summary>Sub-item of a transaction (e.g. one receipt position). The first UI version uses a single line.</summary>
public sealed record TransactionLine
{
    public Guid Id { get; init; }
    public Guid? CategoryId { get; init; }
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
}

public sealed record Transaction
{
    public Guid Id { get; init; }
    public DateOnly Date { get; init; }
    public TransactionType Type { get; init; }
    /// <summary>Amount in <see cref="CurrencyCode"/>, always positive.</summary>
    public decimal Amount { get; init; }
    public string CurrencyCode { get; init; } = "PLN";
    public decimal ExchangeRateToPln { get; init; } = 1m;
    /// <summary>Amount converted to PLN and stored at entry time; all reports use this value.</summary>
    public decimal AmountPln { get; init; }
    public Guid? CategoryId { get; init; }
    public string Note { get; init; } = "";
    public Guid? EventId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedByDeviceId { get; init; } = "";
    public IReadOnlyList<TransactionLine> Lines { get; init; } = [];
}

public sealed record Category
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public Guid? ParentId { get; init; }
    public CategoryType Type { get; init; } = CategoryType.Expense;
    public string Color { get; init; } = "";
    public string Icon { get; init; } = "";
    public int SortOrder { get; init; }
    public bool IsArchived { get; init; }
}

/// <summary>A special event (e.g. holidays): a dimension next to categories.</summary>
public sealed record SpecialEvent
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public DateOnly DateFrom { get; init; }
    public DateOnly DateTo { get; init; }
    public string Color { get; init; } = "";
    public decimal? PlannedBudgetPln { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>Monthly limit for a category, valid from <see cref="ValidFromMonth"/> (first day of the month) until superseded.</summary>
public sealed record Budget
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public decimal MonthlyAmount { get; init; }
    public DateOnly ValidFromMonth { get; init; }
}

public sealed record RecurringTransaction
{
    public Guid Id { get; init; }
    public TransactionType Type { get; init; }
    public decimal Amount { get; init; }
    public string CurrencyCode { get; init; } = "PLN";
    public Guid? CategoryId { get; init; }
    public string Note { get; init; } = "";
    public Guid? EventId { get; init; }
    public RecurrenceFrequency Frequency { get; init; } = RecurrenceFrequency.Monthly;
    /// <summary>Weekly: ISO day of week 1-7. Monthly: day of month 1-31 (clamped to month length). Yearly: day of month (month taken from <see cref="StartDate"/>).</summary>
    public int Day { get; init; } = 1;
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public DateOnly? LastGeneratedDate { get; init; }
}

/// <summary>Learned mapping from a note pattern to a category (and optionally an event).</summary>
public sealed record CategoryRule
{
    public Guid Id { get; init; }
    public string Pattern { get; init; } = "";
    public Guid CategoryId { get; init; }
    public Guid? EventId { get; init; }
    public int UseCount { get; init; }
}

public enum ExchangeRateSource { Nbp, Manual }

/// <summary>Locally cached exchange rate (not synchronised between devices).</summary>
public sealed record ExchangeRate
{
    public string CurrencyCode { get; init; } = "";
    /// <summary>Effective date of the rate (last NBP working day before the transaction date).</summary>
    public DateOnly Date { get; init; }
    public decimal Rate { get; init; }
    public ExchangeRateSource Source { get; init; } = ExchangeRateSource.Nbp;
    public DateTimeOffset FetchedAt { get; init; }
}
