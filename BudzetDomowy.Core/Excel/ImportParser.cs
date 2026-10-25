using System.Globalization;
using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Core.Excel;

public enum SignMode
{
    /// <summary>Every row is an expense (like the monthly sheets); a negative amount is reported as a problem.</summary>
    AllExpenses,
    /// <summary>Bank-statement style: minus is an expense, plus is income.</summary>
    NegativeIsExpense,
}

public sealed record ImportOptions
{
    public SignMode Sign { get; init; } = SignMode.AllExpenses;

    /// <summary>
    /// When set, a cell with this text near the top of a sheet (e.g. "Przychody") and the number next to it become one
    /// income entry on the first day of the sheet's month.
    /// </summary>
    public string? IncomeLabel { get; init; }

    /// <summary>
    /// An address such as "E3": on a sheet where the label is not found, the income of the month is taken from this cell.
    /// </summary>
    public string? IncomeCell { get; init; }

    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Today);
}

public enum ImportRowStatus
{
    /// <summary>Will be added.</summary>
    New,
    /// <summary>Will be added, but something is odd (e.g. no category).</summary>
    Warning,
    /// <summary>The same entry is already in the program.</summary>
    Duplicate,
    /// <summary>Not a transaction: empty, repeated heading or a subtotal.</summary>
    Skipped,
    /// <summary>Cannot be imported (no date, bad amount, foreign currency...).</summary>
    Error,
}

public sealed record ImportRow(
    string Sheet, int ExcelRow, DateOnly? Date, decimal Amount, TransactionType Type,
    string Place, string Note, string SourceCategory, ImportRowStatus Status, string Message)
{
    /// <summary>What the amount cell held (the text as it was), to help correct a row that could not be read.</summary>
    public string RawAmount { get; init; } = "";

    /// <summary>A category chosen while correcting the row; it is used instead of the mapping of the file's category names.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Corrected by the user in the preview.</summary>
    public bool IsEdited { get; init; }

    public bool WillBeImported => Status is ImportRowStatus.New or ImportRowStatus.Warning;
    public bool IsFromSheetIncome => SourceCategory == ImportParser.SheetIncomeCategory;
}

public static class ImportParser
{
    /// <summary>Pseudo category name of income taken from a labelled cell of a sheet.</summary>
    public const string SheetIncomeCategory = "(przychód z arkusza)";

    private static readonly string[] SubtotalWords = ["razem", "suma", "podsumowanie", "ogolem", "narastajaco", "total", "srednio", "bilans"];
    private static readonly string[] IncomeWords = ["przychod", "wplyw", "wplata", "income", "+"];
    private static readonly string[] ExpenseWords = ["wydatek", "wyplata", "obciazenie", "expense", "-"];

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "yyyy.MM.dd"];

    public static bool TryReadDate(object? cell, out DateOnly date)
    {
        date = default;
        switch (cell)
        {
            case DateTime d when d.Year is >= 1990 and <= 2100:
                date = DateOnly.FromDateTime(d);
                return true;
            case string s when DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed):
                date = DateOnly.FromDateTime(parsed);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads an amount cell: a number, or text such as "12,50 zł" or "9 EUR".</summary>
    public static bool TryReadAmount(object? cell, out decimal amount, out string? currency, out bool negative)
    {
        amount = 0;
        currency = null;
        negative = false;
        switch (cell)
        {
            case double d:
                negative = d < 0;
                amount = Math.Round((decimal)Math.Abs(d), 2, MidpointRounding.AwayFromZero);
                return true;
            case string s when !string.IsNullOrWhiteSpace(s):
                var text = s.Trim();
                if (text.StartsWith('-') || text.StartsWith('−')) { negative = true; text = text[1..]; }
                else if (text.StartsWith('+')) text = text[1..];
                if (text.StartsWith('(') && text.EndsWith(')')) { negative = true; text = text[1..^1]; }
                if (!AmountParser.TryParseWithCurrency(text, CurrencyCatalog.Codes, out amount, out currency)) return false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Turns the rows of one sheet into import rows. Duplicates and categories are decided later.</summary>
    public static IReadOnlyList<ImportRow> Parse(SheetData sheet, ColumnMap map, DateOnly? sheetMonth, ImportOptions options)
    {
        var rows = new List<ImportRow>();
        DateOnly? lastDate = null;
        var raw = "";
        void Add(ImportRow row) => rows.Add(row with { RawAmount = raw });

        for (var r = map.FirstDataRow; r < sheet.RowCount; r++)
        {
            var excelRow = r + 1;
            var place = map.Place is { } p ? sheet.Text(r, p) : "";
            var note = map.Note is { } n ? sheet.Text(r, n) : "";
            var category = map.Category is { } c ? sheet.Text(r, c) : "";

            // the date is written once per day: following rows without a date belong to the same day
            if (map.Date is { } dc && TryReadDate(sheet.At(r, dc), out var d)) lastDate = d;

            var rawAmount = map.Amount is { } a ? sheet.At(r, a) : null;
            raw = SheetData.TextOf(rawAmount);
            var hasText = place.Length > 0 || note.Length > 0 || category.Length > 0;

            if (IsSubtotal(place, note, category))
            {
                if (rawAmount is not null)
                    Add(Row(sheet, excelRow, lastDate, 0, TransactionType.Expense, place, note, category, ImportRowStatus.Skipped, "subtotal"));
                continue;
            }

            if (!TryReadAmount(rawAmount, out var amount, out var currency, out var negative))
            {
                if (rawAmount is not null && SheetData.TextOf(rawAmount).Length > 0 && !IsHeadingRepeat(rawAmount))
                    Add(Row(sheet, excelRow, lastDate, 0, TransactionType.Expense, place, note, category, ImportRowStatus.Error, "bad-amount"));
                else if (rawAmount is null && hasText && (map.Amount is not null))
                    Add(Row(sheet, excelRow, lastDate, 0, TransactionType.Expense, place, note, category, ImportRowStatus.Skipped, "no-amount"));
                continue; // empty rows are not worth listing
            }

            if (amount == 0)
            {
                Add(Row(sheet, excelRow, lastDate, 0, TransactionType.Expense, place, note, category, ImportRowStatus.Skipped, "zero"));
                continue;
            }

            if (currency is not null && currency != CurrencyCatalog.Pln)
            {
                Add(Row(sheet, excelRow, lastDate, amount, TransactionType.Expense, place, note, category, ImportRowStatus.Error, "foreign-currency"));
                continue;
            }

            var type = ResolveType(sheet, r, map, negative, options.Sign, out var typeProblem);
            if (typeProblem is not null)
            {
                Add(Row(sheet, excelRow, lastDate, amount, TransactionType.Expense, place, note, category, ImportRowStatus.Error, typeProblem));
                continue;
            }

            if (lastDate is not { } date)
            {
                Add(Row(sheet, excelRow, null, amount, type, place, note, category, ImportRowStatus.Error, "no-date"));
                continue;
            }
            if (date > options.Today.AddYears(5))
            {
                Add(Row(sheet, excelRow, date, amount, type, place, note, category, ImportRowStatus.Error, "date-far-future"));
                continue;
            }

            var status = category.Length == 0 ? ImportRowStatus.Warning : ImportRowStatus.New;
            Add(Row(sheet, excelRow, date, amount, type, place, note, category, status, category.Length == 0 ? "no-category" : ""));
        }

        ImportRow? income = null;
        if (options.IncomeLabel is { Length: > 0 } label) income = ReadLabelledIncome(sheet, label, sheetMonth);
        if (income is null && options.IncomeCell is { Length: > 0 } cell) income = ReadIncomeCell(sheet, cell, sheetMonth);
        if (income is not null) rows.Add(income);

        return rows;
    }

    private static ImportRow Row(SheetData sheet, int excelRow, DateOnly? date, decimal amount, TransactionType type,
        string place, string note, string category, ImportRowStatus status, string message) =>
        new(sheet.Name, excelRow, date, amount, type, place, note, category, status, message);

    private static bool IsHeadingRepeat(object? value) =>
        value is string s && AmountHeadsContain(TextNorm.Normalize(s));

    private static bool AmountHeadsContain(string normalized) => normalized is "kwota" or "amount" or "wartosc";

    private static bool IsSubtotal(string place, string note, string category)
    {
        foreach (var text in new[] { place, note, category })
        {
            var n = TextNorm.Normalize(text);
            if (n.Length == 0) continue;
            if (SubtotalWords.Any(w => n == w || n.StartsWith(w + " ", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static TransactionType ResolveType(SheetData sheet, int row, ColumnMap map, bool negative, SignMode sign, out string? problem)
    {
        problem = null;
        if (map.Type is { } tc && TextNorm.Normalize(sheet.Text(row, tc)) is { Length: > 0 } t)
        {
            if (IncomeWords.Contains(t)) return TransactionType.Income;
            if (ExpenseWords.Contains(t)) return TransactionType.Expense;
        }

        if (sign == SignMode.NegativeIsExpense) return negative ? TransactionType.Expense : TransactionType.Income;
        if (negative) problem = "negative";
        return TransactionType.Expense;
    }

    /// <summary>
    /// The income of the month from a cell the user pointed at. An empty cell means "no income on this sheet"; a cell that holds
    /// something else than a positive amount is reported.
    /// </summary>
    public static ImportRow? ReadIncomeCell(SheetData sheet, string address, DateOnly? sheetMonth)
    {
        if (!SheetData.TryParseAddress(address, out var row, out var column)) return null;
        var cell = sheet.At(row, column);
        var raw = SheetData.TextOf(cell);
        if (raw.Length == 0) return null;

        ImportRow Problem(string message, decimal amount = 0) =>
            new(sheet.Name, row + 1, sheetMonth, amount, TransactionType.Income, "", sheet.Name, SheetIncomeCategory, ImportRowStatus.Error, message) { RawAmount = raw };

        if (!TryReadAmount(cell, out var value, out var currency, out var negative) || negative || value <= 0) return Problem("income-cell");
        if (currency is not null && currency != CurrencyCatalog.Pln) return Problem("foreign-currency", value);
        if (sheetMonth is not { } month) return Problem("no-date", value);
        return new ImportRow(sheet.Name, row + 1, month, value, TransactionType.Income, "", sheet.Name, SheetIncomeCategory, ImportRowStatus.New, "") { RawAmount = raw };
    }

    public static ImportRow? ReadLabelledIncome(SheetData sheet, string label, DateOnly? sheetMonth)
    {
        var wanted = TextNorm.Normalize(label);
        for (var r = 0; r < Math.Min(sheet.RowCount, 12); r++)
        {
            for (var c = 0; c < Math.Min(sheet.Rows[r].Length, 20); c++)
            {
                if (TextNorm.Normalize(sheet.Text(r, c)) != wanted) continue;
                // Next to the label there can be several figures ("narastająco", "średnio", "w miesiącu"):
                // the one under a heading that mentions the month is the month's own value.
                var monthColumn = Enumerable.Range(c + 1, 7)
                    .Where(k => Enumerable.Range(0, r).Any(h => TextNorm.Normalize(sheet.Text(h, k)).Contains("miesiac", StringComparison.Ordinal)))
                    .Cast<int?>().FirstOrDefault();
                var candidates = monthColumn is { } mc ? [mc] : Enumerable.Range(c + 1, 7);
                foreach (var k in candidates)
                {
                    if (sheet.At(r, k) is not double v || v <= 0) continue;
                    if (sheetMonth is not { } month)
                        return new ImportRow(sheet.Name, r + 1, null, (decimal)v, TransactionType.Income, "", "", SheetIncomeCategory, ImportRowStatus.Error, "no-date");
                    return new ImportRow(sheet.Name, r + 1, month, Math.Round((decimal)v, 2, MidpointRounding.AwayFromZero), TransactionType.Income,
                        "", sheet.Name, SheetIncomeCategory, ImportRowStatus.New, "");
                }
            }
        }
        return null;
    }
}
