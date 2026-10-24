using System.Globalization;
using System.Text;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using ClosedXML.Excel;

namespace BudzetDomowy.Core.Excel;

public sealed record CsvOptions
{
    /// <summary>Semicolon and decimal comma are what Polish Excel opens correctly with a double click.</summary>
    public char Separator { get; init; } = ';';
    public bool DecimalComma { get; init; } = true;
}

public enum ReportScope { Month, Year }

/// <summary>Writes transactions and reports to .xlsx and .csv.</summary>
public static class ExportService
{
    private static readonly string[] Headers =
    [
        "Data", "Rodzaj", "Kwota PLN", "Waluta", "Kwota w walucie", "Kurs", "Kategoria główna", "Podkategoria", "Miejsce", "Notatka", "Wydarzenie",
    ];

    private sealed record Line(Transaction T, string MainCategory, string SubCategory, string EventName);

    private static IEnumerable<Line> Lines(IEnumerable<Transaction> transactions, IEnumerable<Category> categories, IEnumerable<SpecialEvent> events)
    {
        var cats = categories.ToDictionary(c => c.Id);
        var evs = events.ToDictionary(e => e.Id);
        foreach (var t in transactions.OrderBy(t => t.Date).ThenBy(t => t.CreatedAt))
        {
            var (main, sub) = Split(t.CategoryId, cats);
            yield return new Line(t, main, sub, t.EventId is { } e && evs.TryGetValue(e, out var ev) ? ev.Name : "");
        }
    }

    private static (string Main, string Sub) Split(Guid? id, IReadOnlyDictionary<Guid, Category> cats)
    {
        if (id is not { } current || !cats.TryGetValue(current, out var c)) return ("", "");
        if (c.ParentId is { } p && cats.TryGetValue(p, out var parent)) return (parent.Name, c.Name);
        return (c.Name, "");
    }

    /// <summary>CSV only: text that starts like a formula gets a leading apostrophe so Excel shows it as text instead of calculating it.</summary>
    public static string Guard(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    // ----- transactions to Excel -----

    public static void TransactionsToXlsx(string path, IEnumerable<Transaction> transactions, IEnumerable<Category> categories, IEnumerable<SpecialEvent> events)
    {
        var lines = Lines(transactions, categories, events).ToList();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Transakcje");

        for (var c = 0; c < Headers.Length; c++) ws.Cell(1, c + 1).Value = Headers[c];
        var row = 2;
        foreach (var l in lines)
        {
            var t = l.T;
            ws.Cell(row, 1).Value = t.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 2).Value = t.Type == TransactionType.Income ? "Przychód" : "Wydatek";
            ws.Cell(row, 3).Value = t.AmountPln;
            ws.Cell(row, 4).Value = t.CurrencyCode;
            ws.Cell(row, 5).Value = t.Amount;
            ws.Cell(row, 6).Value = t.ExchangeRateToPln;
            // strings are stored as text cells, so Excel never evaluates them as formulas
            ws.Cell(row, 7).Value = l.MainCategory;
            ws.Cell(row, 8).Value = l.SubCategory;
            ws.Cell(row, 9).Value = t.Place;
            ws.Cell(row, 10).Value = t.Note;
            ws.Cell(row, 11).Value = l.EventName;
            row++;
        }

        var last = Math.Max(row - 1, 1);
        ws.Range(2, 1, last, 1).Style.DateFormat.Format = "dd.mm.yyyy";
        ws.Range(2, 3, last, 3).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(2, 5, last, 5).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(2, 6, last, 6).Style.NumberFormat.Format = "0.0000";
        StyleHeader(ws.Range(1, 1, 1, Headers.Length));
        ws.SheetView.FreezeRows(1);
        if (lines.Count > 0) ws.Range(1, 1, last, Headers.Length).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(lines.Count + 1, 200));
        foreach (var c in new[] { 9, 10 }) if (ws.Column(c).Width > 50) ws.Column(c).Width = 50;
        wb.SaveAs(path);
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#F57C00");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    // ----- transactions to CSV -----

    public static void TransactionsToCsv(string path, IEnumerable<Transaction> transactions, IEnumerable<Category> categories,
        IEnumerable<SpecialEvent> events, CsvOptions? options = null)
    {
        File.WriteAllText(path, TransactionsToCsvText(transactions, categories, events, options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string TransactionsToCsvText(IEnumerable<Transaction> transactions, IEnumerable<Category> categories,
        IEnumerable<SpecialEvent> events, CsvOptions? options = null)
    {
        options ??= new CsvOptions();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(options.Separator, Headers.Select(h => Quote(h, options.Separator))));

        string Num(decimal v, string format) =>
            v.ToString(format, options.DecimalComma ? new CultureInfo("pl-PL") { NumberFormat = { NumberGroupSeparator = "" } } : CultureInfo.InvariantCulture);

        foreach (var l in Lines(transactions, categories, events))
        {
            var t = l.T;
            var fields = new[]
            {
                t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.Type == TransactionType.Income ? "Przychód" : "Wydatek",
                Num(t.AmountPln, "0.00"),
                t.CurrencyCode,
                Num(t.Amount, "0.00"),
                Num(t.ExchangeRateToPln, "0.0000"),
                Guard(l.MainCategory), Guard(l.SubCategory), Guard(t.Place), Guard(t.Note), Guard(l.EventName),
            };
            sb.AppendLine(string.Join(options.Separator, fields.Select(f => Quote(f, options.Separator))));
        }
        return sb.ToString();
    }

    private static string Quote(string field, char separator) =>
        field.IndexOfAny([separator, '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    // ----- reports -----

    /// <summary>Summary with comparison, category breakdown and monthly trend of one month or year, as an Excel file.</summary>
    public static void ReportToXlsx(string path, ReportScope scope, DateOnly anchor, IReadOnlyList<Transaction> all, IReadOnlyList<Category> categories)
    {
        DateOnly from, to;
        PeriodComparison cmp;
        string title, previousTitle;
        if (scope == ReportScope.Year)
        {
            from = new DateOnly(anchor.Year, 1, 1);
            to = new DateOnly(anchor.Year, 12, 31);
            cmp = PeriodReports.CompareYear(all, anchor.Year);
            title = anchor.Year.ToString(CultureInfo.InvariantCulture);
            previousTitle = (anchor.Year - 1).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            from = new DateOnly(anchor.Year, anchor.Month, 1);
            to = PeriodReports.MonthEnd(anchor);
            cmp = PeriodReports.CompareMonth(all, anchor);
            title = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            previousTitle = from.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        using var wb = new XLWorkbook();

        // summary
        var s = wb.AddWorksheet("Podsumowanie");
        s.Cell(1, 1).Value = "Raport za okres:";
        s.Cell(1, 2).Value = title;
        s.Cell(1, 1).Style.Font.Bold = true;
        var headers = new[] { "", title, previousTitle, "Zmiana", "Zmiana %" };
        for (var c = 0; c < headers.Length; c++) s.Cell(3, c + 1).Value = headers[c];
        StyleHeader(s.Range(3, 1, 3, headers.Length));
        var rows = new[] { ("Przychody", cmp.Income), ("Wydatki", cmp.Expense), ("Bilans", cmp.Balance) };
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, change) = rows[i];
            var r = 4 + i;
            s.Cell(r, 1).Value = label;
            s.Cell(r, 2).Value = change.Current;
            s.Cell(r, 3).Value = change.Previous;
            s.Cell(r, 4).Value = change.Delta;
            if (change.Percent is { } p) s.Cell(r, 5).Value = p; else s.Cell(r, 5).Value = "—";
        }
        s.Range(4, 2, 6, 4).Style.NumberFormat.Format = "#,##0.00";
        s.Range(4, 5, 6, 5).Style.NumberFormat.Format = "0.0%";
        s.Columns().AdjustToContents();

        // categories
        var k = wb.AddWorksheet("Kategorie");
        var catHeaders = new[] { "Kategoria", "Podkategoria", "Kwota PLN", "Udział" };
        for (var c = 0; c < catHeaders.Length; c++) k.Cell(1, c + 1).Value = catHeaders[c];
        StyleHeader(k.Range(1, 1, 1, catHeaders.Length));
        var kr = 2;
        foreach (var root in PeriodReports.Breakdown(all, categories, from, to, TransactionType.Expense))
        {
            var name = root.Name.Length == 0 ? "Bez kategorii" : root.Name;
            k.Cell(kr, 1).Value = name;
            k.Cell(kr, 3).Value = root.Amount;
            k.Cell(kr, 4).Value = root.Share;
            k.Range(kr, 1, kr, 4).Style.Font.Bold = true;
            kr++;
            foreach (var child in root.Children)
            {
                k.Cell(kr, 1).Value = name;
                k.Cell(kr, 2).Value = child.IsOwn ? "(bez podkategorii)" : child.Name;
                k.Cell(kr, 3).Value = child.Amount;
                k.Cell(kr, 4).Value = child.Amount == 0 || root.Amount == 0 ? 0 : child.Amount / root.Amount;
                kr++;
            }
        }
        k.Range(2, 3, Math.Max(kr, 2), 3).Style.NumberFormat.Format = "#,##0.00";
        k.Range(2, 4, Math.Max(kr, 2), 4).Style.NumberFormat.Format = "0.0%";
        k.Columns().AdjustToContents();

        // trend
        var firstMonth = scope == ReportScope.Year ? new DateOnly(anchor.Year, 1, 1) : from.AddMonths(-11);
        var lastMonth = scope == ReportScope.Year ? new DateOnly(anchor.Year, 12, 1) : from;
        var tr = wb.AddWorksheet("Trend");
        var trHeaders = new[] { "Miesiąc", "Przychody", "Wydatki", "Bilans" };
        for (var c = 0; c < trHeaders.Length; c++) tr.Cell(1, c + 1).Value = trHeaders[c];
        StyleHeader(tr.Range(1, 1, 1, trHeaders.Length));
        var tRow = 2;
        foreach (var p in PeriodReports.MonthlyTrend(all, firstMonth, lastMonth))
        {
            tr.Cell(tRow, 1).Value = p.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            tr.Cell(tRow, 2).Value = p.Income;
            tr.Cell(tRow, 3).Value = p.Expense;
            tr.Cell(tRow, 4).Value = p.Balance;
            tRow++;
        }
        tr.Range(2, 2, tRow, 4).Style.NumberFormat.Format = "#,##0.00";
        tr.Columns().AdjustToContents();

        wb.SaveAs(path);
    }
}
