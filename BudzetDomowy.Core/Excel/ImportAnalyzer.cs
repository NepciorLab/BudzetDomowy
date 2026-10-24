using System.Globalization;
using System.Text;

namespace BudzetDomowy.Core.Excel;

/// <summary>Zero-based columns; <see cref="HeaderRow"/> is the zero-based row of the headings (data starts on the next row).</summary>
public sealed record ColumnMap(int HeaderRow, int? Date, int? Place, int? Category, int? Note, int? Amount, int? Type)
{
    public int FirstDataRow => HeaderRow + 1;
}

public sealed record SheetAnalysis(string Name, bool LooksLikeData, ColumnMap? Map, int DataRows, DateOnly? SheetMonth, string Reason);

public static class TextNorm
{
    /// <summary>Lower case, no diacritics, single spaces: "  Ubrania/Buty " and "ubrania/buty" are the same.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Trim().ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Finds out whether a worksheet holds transactions and which column is which.</summary>
public static class ImportAnalyzer
{
    private static readonly string[] DateHeads = ["data", "date", "dzien"];
    private static readonly string[] PlaceHeads = ["gdzie", "miejsce", "sklep", "place", "odbiorca", "kontrahent", "nazwa"];
    private static readonly string[] CategoryHeads = ["kategoria", "category", "kategoria wydatku"];
    private static readonly string[] NoteHeads = ["opis", "notatka", "uwagi", "uwaga", "note", "description", "tytul", "komentarz"];
    private static readonly string[] AmountHeads = ["kwota", "amount", "wartosc", "kwota pln"];
    private static readonly string[] TypeHeads = ["typ", "type", "rodzaj transakcji"];

    private const int HeaderSearchRows = 30;
    private const int SampleRows = 600;

    public static IReadOnlyList<SheetAnalysis> AnalyzeAll(IEnumerable<SheetData> sheets) => sheets.Select(Analyze).ToList();

    public static SheetAnalysis Analyze(SheetData sheet)
    {
        var month = FindSheetMonth(sheet);
        if (sheet.RowCount == 0) return new SheetAnalysis(sheet.Name, false, null, 0, month, "empty");

        var header = FindHeaderRow(sheet);
        if (header is null) return new SheetAnalysis(sheet.Name, false, null, 0, month, "no-header");

        var (row, cols) = header.Value;
        int? Pick(string[] heads) => cols.TryGetValue(heads, out var c) ? c : null;
        var amount = Pick(AmountHeads);
        var category = Pick(CategoryHeads);
        var place = Pick(PlaceHeads);
        var note = Pick(NoteHeads);
        var type = Pick(TypeHeads);

        // The date is recognised by what is in the column, not by its heading: in a day-per-row sheet the heading
        // "Data" may sit over day numbers while the real dates are in the neighbouring column.
        var date = DetectDateColumn(sheet, row + 1, amount, Pick(DateHeads));
        var map = new ColumnMap(row, date, place, category, note, amount, type);

        var dataRows = 0;
        for (var r = map.FirstDataRow; r < Math.Min(sheet.RowCount, map.FirstDataRow + 5000); r++)
            if (amount is { } a && sheet.At(r, a) is double or string && ImportParser.TryReadAmount(sheet.At(r, a), out _, out _, out _)) dataRows++;

        if (date is null) return new SheetAnalysis(sheet.Name, false, map, dataRows, month, "no-date");
        if (dataRows == 0) return new SheetAnalysis(sheet.Name, false, map, 0, month, "no-rows");
        return new SheetAnalysis(sheet.Name, true, map, dataRows, month, "");
    }

    private static (int Row, Dictionary<string[], int> Columns)? FindHeaderRow(SheetData sheet)
    {
        var groups = new[] { DateHeads, PlaceHeads, CategoryHeads, NoteHeads, AmountHeads, TypeHeads };
        for (var r = 0; r < Math.Min(sheet.RowCount, HeaderSearchRows); r++)
        {
            var found = new Dictionary<string[], int>();
            for (var c = 0; c < Math.Min(sheet.Rows[r].Length, WorkbookLoader.MaxColumns); c++)
            {
                if (sheet.At(r, c) is not string text) continue;
                var n = TextNorm.Normalize(text);
                foreach (var g in groups)
                    if (g.Contains(n) && !found.ContainsKey(g)) found[g] = c;
            }
            if (found.ContainsKey(AmountHeads) && (found.ContainsKey(CategoryHeads) || found.ContainsKey(PlaceHeads) || found.ContainsKey(NoteHeads)))
                return (r, found);
        }
        return null;
    }

    private static int? DetectDateColumn(SheetData sheet, int firstRow, int? amountColumn, int? headedDate)
    {
        var best = -1;
        var bestCount = 0;
        for (var c = 0; c < Math.Min(sheet.ColumnCount, WorkbookLoader.MaxColumns); c++)
        {
            var count = 0;
            for (var r = firstRow; r < Math.Min(sheet.RowCount, firstRow + SampleRows); r++)
                if (ImportParser.TryReadDate(sheet.At(r, c), out _)) count++;
            if (count > bestCount) (best, bestCount) = (c, count);
        }
        if (best >= 0 && bestCount >= 1) return best;
        return headedDate;
    }

    /// <summary>The month a sheet is about: the first real date near the top ("styczeń 2026" sheets keep it in a cell).</summary>
    public static DateOnly? FindSheetMonth(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.RowCount, 8); r++)
            for (var c = 0; c < Math.Min(sheet.Rows[r].Length, 12); c++)
                if (sheet.At(r, c) is DateTime d) return new DateOnly(d.Year, d.Month, 1);
        return null;
    }
}
