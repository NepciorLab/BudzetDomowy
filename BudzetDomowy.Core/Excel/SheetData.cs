using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace BudzetDomowy.Core.Excel;

/// <summary>A worksheet as plain values (DateTime, double, string, bool or null), so import logic needs no Excel at all.</summary>
public sealed class SheetData
{
    public string Name { get; }
    public object?[][] Rows { get; }

    public SheetData(string name, object?[][] rows)
    {
        Name = name;
        Rows = rows;
    }

    public int RowCount => Rows.Length;
    public int ColumnCount => Rows.Length == 0 ? 0 : Rows.Max(r => r.Length);

    /// <summary>Zero-based access; anything outside the used range is null.</summary>
    public object? At(int row, int column) =>
        row >= 0 && row < Rows.Length && column >= 0 && column < Rows[row].Length ? Rows[row][column] : null;

    public string Text(int row, int column) => TextOf(At(row, column));

    public static string TextOf(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double n => n.ToString("0.############", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>"A", "B", ... "AA" for a zero-based column index.</summary>
    public static string ColumnLetter(int index)
    {
        var sb = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }
}

public static class WorkbookLoader
{
    public const int MaxColumns = 60;

    /// <summary>
    /// Reads every worksheet of an .xlsx/.xlsm file (macros are ignored and never run). Formulas give their last
    /// calculated value. The file is opened read-only, so it may be open in Excel at the same time.
    /// </summary>
    public static IReadOnlyList<SheetData> Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var workbook = new XLWorkbook(stream);

        var sheets = new List<SheetData>();
        foreach (var ws in workbook.Worksheets)
        {
            var used = ws.RangeUsed(XLCellsUsedOptions.Contents);
            if (used is null)
            {
                sheets.Add(new SheetData(ws.Name, []));
                continue;
            }

            var lastRow = used.LastRow().RowNumber();
            var lastCol = Math.Min(used.LastColumn().ColumnNumber(), MaxColumns);
            var rows = new object?[lastRow][];
            for (var r = 1; r <= lastRow; r++)
            {
                var row = new object?[lastCol];
                for (var c = 1; c <= lastCol; c++) row[c - 1] = ValueOf(ws.Cell(r, c));
                rows[r - 1] = row;
            }
            sheets.Add(new SheetData(ws.Name, rows));
        }
        return sheets;
    }

    private static object? ValueOf(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        XLCellValue value;
        try { value = cell.HasFormula ? cell.CachedValue : cell.Value; }
        catch (Exception) { return null; }

        if (value.IsBlank) return null;
        if (value.IsDateTime) return value.GetDateTime();
        if (value.IsNumber) return value.GetNumber();
        if (value.IsBoolean) return value.GetBoolean();
        if (value.IsTimeSpan) return value.GetTimeSpan().ToString();
        if (value.IsText) return value.GetText();
        return null; // errors such as #REF!
    }
}
