using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Excel;

/// <summary>What to do with a category name found in the file: use an existing category, create a new one, or leave entries uncategorised.</summary>
public sealed record CategoryChoice(Guid? ExistingId, string? NewName)
{
    public static readonly CategoryChoice Uncategorized = new(null, null);
    public bool IsExisting => ExistingId is not null;
    public bool IsNew => NewName is not null;
}

public sealed record SheetSelection(SheetData Sheet, ColumnMap Map, DateOnly? SheetMonth);

public sealed record ImportPlan(IReadOnlyList<ImportRow> Rows)
{
    public int ToImport => Rows.Count(r => r.WillBeImported);
    public int Duplicates => Rows.Count(r => r.Status == ImportRowStatus.Duplicate);
    public int Errors => Rows.Count(r => r.Status == ImportRowStatus.Error);
    public int Skipped => Rows.Count(r => r.Status == ImportRowStatus.Skipped);
    public int Warnings => Rows.Count(r => r.Status == ImportRowStatus.Warning);

    /// <summary>Category names used by rows that will be imported, with the number of rows, most used first.</summary>
    public IReadOnlyList<(string Name, int Count)> SourceCategories => Rows
        .Where(r => r.WillBeImported && r.SourceCategory.Length > 0)
        .GroupBy(r => TextNorm.Normalize(r.SourceCategory))
        .Select(g => (Name: g.First().SourceCategory, Count: g.Count()))
        .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}

public sealed record ImportReport(int Added, int DuplicatesSkipped, int Errors, int Skipped, int CategoriesCreated);

public static class CategoryMatcher
{
    /// <summary>The best guess for a category name from the file: the program's category of the same name, else a new one.</summary>
    public static CategoryChoice Suggest(string sourceName, IEnumerable<Category> categories, TransactionType type)
    {
        var wanted = TextNorm.Normalize(sourceName);
        if (wanted.Length == 0) return CategoryChoice.Uncategorized;
        if (sourceName == ImportParser.SheetIncomeCategory)
        {
            var other = categories.FirstOrDefault(c => !c.IsArchived && c.Type != CategoryType.Expense && c.ParentId is null && TextNorm.Normalize(c.Name).StartsWith("inne"));
            return other is not null ? new CategoryChoice(other.Id, null) : new CategoryChoice(null, "Inne przychody");
        }

        var all = categories.ToList();
        var matches = all.Where(c => !c.IsArchived && TextNorm.Normalize(c.Name) == wanted && CategoryHelpers.Matches(c.Type, type)).ToList();
        // the top-level one wins when a subcategory happens to have the same name
        var best = matches.OrderBy(c => c.ParentId is null ? 0 : 1).FirstOrDefault();
        return best is not null ? new CategoryChoice(best.Id, null) : new CategoryChoice(null, sourceName.Trim());
    }
}

public sealed class ImportService
{
    private readonly LocalReader _reader;
    private readonly TransactionService _transactions;
    private readonly CategoryService _categories;

    public ImportService(LocalReader reader, TransactionService transactions, CategoryService categories)
    {
        _reader = reader;
        _transactions = transactions;
        _categories = categories;
    }

    /// <summary>Key that tells whether two entries are "the same": date, amount, place and note (not the category, which may be edited later).</summary>
    public static string KeyOf(DateOnly date, decimal amountPln, TransactionType type, string place, string note) =>
        string.Join('|', date.ToString("yyyy-MM-dd"), type, Money.ToCents(amountPln), TextNorm.Normalize(place), TextNorm.Normalize(note));

    /// <summary>
    /// Parses the chosen sheets and marks entries that are already in the program. Duplicates are counted, not merged:
    /// if the file has the same purchase three times and the program already has two, one is added. That keeps real
    /// repeated purchases while a second import of the same file adds nothing.
    /// </summary>
    public ImportPlan Plan(IEnumerable<SheetSelection> sheets, ImportOptions options)
    {
        var rows = new List<ImportRow>();
        foreach (var s in sheets) rows.AddRange(ImportParser.Parse(s.Sheet, s.Map, s.SheetMonth, options));

        var existing = new Dictionary<string, int>();
        foreach (var t in _reader.GetTransactions())
        {
            var key = KeyOf(t.Date, t.AmountPln, t.Type, t.Place, t.Note);
            existing[key] = existing.GetValueOrDefault(key) + 1;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.WillBeImported || row.Date is not { } date) continue;
            var key = KeyOf(date, row.Amount, row.Type, row.Place, row.Note);
            if (existing.GetValueOrDefault(key) > 0)
            {
                existing[key]--;
                rows[i] = row with { Status = ImportRowStatus.Duplicate, Message = "duplicate" };
            }
        }
        return new ImportPlan(rows);
    }

    /// <summary>Suggested category choice for every category name in the plan.</summary>
    public IReadOnlyDictionary<string, CategoryChoice> SuggestMapping(ImportPlan plan, IReadOnlyDictionary<string, CategoryChoice>? remembered = null)
    {
        var categories = _reader.GetCategories();
        var map = new Dictionary<string, CategoryChoice>();
        foreach (var (name, _) in plan.SourceCategories)
        {
            var key = TextNorm.Normalize(name);
            if (remembered is not null && remembered.TryGetValue(key, out var known) && StillValid(known, categories))
            {
                map[key] = known;
                continue;
            }
            var type = plan.Rows.FirstOrDefault(r => r.WillBeImported && TextNorm.Normalize(r.SourceCategory) == key)?.Type ?? TransactionType.Expense;
            map[key] = CategoryMatcher.Suggest(name, categories, type);
        }
        return map;
    }

    private static bool StillValid(CategoryChoice choice, IEnumerable<Category> categories) =>
        choice.ExistingId is not { } id || categories.Any(c => c.Id == id && !c.IsArchived);

    /// <summary>Writes the entries as ordinary events (they sync like anything typed by hand).</summary>
    public ImportReport Execute(ImportPlan plan, IReadOnlyDictionary<string, CategoryChoice> mapping, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // create the new categories first (once per name), with the type of the rows that use them
        var created = new Dictionary<string, Guid>();
        var createdCount = 0;
        foreach (var (name, _) in plan.SourceCategories)
        {
            var key = TextNorm.Normalize(name);
            if (!mapping.TryGetValue(key, out var choice) || choice.NewName is not { Length: > 0 } newName) continue;
            var newKey = TextNorm.Normalize(newName);
            if (created.ContainsKey(newKey)) continue;

            var existing = _reader.GetCategories().FirstOrDefault(c => !c.IsArchived && c.ParentId is null && TextNorm.Normalize(c.Name) == newKey);
            if (existing is not null)
            {
                created[newKey] = existing.Id;
                continue;
            }
            var usedAsIncome = plan.Rows.Where(r => r.WillBeImported && TextNorm.Normalize(r.SourceCategory) == key).All(r => r.Type == TransactionType.Income);
            created[newKey] = _categories.Add(newName.Trim(), null, usedAsIncome ? CategoryType.Income : CategoryType.Expense).Id;
            createdCount++;
        }

        var added = 0;
        foreach (var row in plan.Rows.Where(r => r.WillBeImported))
        {
            ct.ThrowIfCancellationRequested();
            Guid? categoryId = null;
            if (row.SourceCategory.Length > 0 && mapping.TryGetValue(TextNorm.Normalize(row.SourceCategory), out var choice))
            {
                if (choice.ExistingId is { } id) categoryId = id;
                else if (choice.NewName is { } n && created.TryGetValue(TextNorm.Normalize(n), out var newId)) categoryId = newId;
            }

            _transactions.Add(row.Date!.Value, row.Type, row.Amount, categoryId, row.Note, null, place: row.Place);
            added++;
            if (added % 25 == 0) progress?.Report(added);
        }
        progress?.Report(added);

        return new ImportReport(added, plan.Duplicates, plan.Errors, plan.Skipped, createdCount);
    }
}
