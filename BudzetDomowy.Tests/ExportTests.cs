using System.Text;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using ClosedXML.Excel;

namespace BudzetDomowy.Tests;

public class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-exp-" + Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly IReadOnlyList<Category> Cats = DefaultCategories.Create();
    private static Guid Cat(string name) => Cats.First(c => c.Name == name).Id;

    private static readonly SpecialEvent Holiday = new() { Id = Guid.NewGuid(), Name = "Wakacje", DateFrom = new DateOnly(2026, 7, 1), DateTo = new DateOnly(2026, 7, 10) };

    private static Transaction[] Data() =>
    [
        Make.Tx("2026-07-02", TransactionType.Expense, 54.30m, Cat("Paliwo")) with { Place = "Orlen", Note = "pełny bak", EventId = Holiday.Id },
        Make.Tx("2026-07-01", TransactionType.Expense, 425.12m, Cat("Spożywcze")) with { Amount = 100m, CurrencyCode = "EUR", ExchangeRateToPln = 4.2512m, Place = "Rewe" },
        Make.Tx("2026-07-03", TransactionType.Income, 1000m, Cat("Wynagrodzenie")) with { Place = "Firma", Note = "=1+1" },
        Make.Tx("2026-07-04", TransactionType.Expense, 5m, null) with { Place = "Kiosk; \"róg\"" },
    ];

    [Fact]
    public void Xlsx_HasOneRowPerTransaction_SortedByDate_WithRealDatesAndNumbers()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        ExportService.TransactionsToXlsx(path, Data(), Cats, [Holiday]);

        var sheet = WorkbookLoader.Load(path).Single();
        Assert.Equal("Transakcje", sheet.Name);
        Assert.Equal(5, sheet.RowCount);
        Assert.Equal("Data", sheet.Text(0, 0));

        Assert.Equal(new DateTime(2026, 7, 1), sheet.At(1, 0));          // the euro purchase is first
        Assert.Equal("Wydatek", sheet.At(1, 1));
        Assert.Equal(425.12, sheet.At(1, 2));
        Assert.Equal("EUR", sheet.At(1, 3));
        Assert.Equal(100.0, sheet.At(1, 4));
        Assert.Equal(4.2512, sheet.At(1, 5));
        Assert.Equal("Spożywcze", sheet.At(1, 6));

        Assert.Equal("Auto", sheet.At(2, 6));
        Assert.Equal("Paliwo", sheet.At(2, 7));
        Assert.Equal("Orlen", sheet.At(2, 8));
        Assert.Equal("pełny bak", sheet.At(2, 9));
        Assert.Equal("Wakacje", sheet.At(2, 10));

        Assert.Equal("Przychód", sheet.At(3, 1));
        Assert.Null(sheet.At(4, 6));                                      // no category: empty cells
    }

    [Fact]
    public void Xlsx_KeepsTextThatLooksLikeAFormulaAsText()
    {
        var path = Path.Combine(_dir, "f.xlsx");
        ExportService.TransactionsToXlsx(path, Data(), Cats, [Holiday]);

        using var wb = new XLWorkbook(path);
        var cell = wb.Worksheet(1).Cell(4, 10);                           // the note "=1+1" (row of the income)
        Assert.False(cell.HasFormula);
        Assert.Equal("=1+1", cell.GetString());
    }

    [Fact]
    public void Xlsx_OfNoTransactions_IsStillAValidFileWithHeadings()
    {
        var path = Path.Combine(_dir, "empty.xlsx");
        ExportService.TransactionsToXlsx(path, [], Cats, []);
        var sheet = WorkbookLoader.Load(path).Single();
        Assert.Equal(1, sheet.RowCount);
        Assert.Equal("Kwota PLN", sheet.Text(0, 2));
    }

    [Fact]
    public void Csv_UsesSemicolons_DecimalCommas_Quotes_AndAGuardAgainstFormulas()
    {
        var text = ExportService.TransactionsToCsvText(Data(), Cats, [Holiday]);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("Data;Rodzaj;Kwota PLN;Waluta;Kwota w walucie;Kurs;", lines[0]);
        Assert.Equal("2026-07-01;Wydatek;425,12;EUR;100,00;4,2512;Spożywcze;;Rewe;;", lines[1]);
        Assert.Contains("'=1+1", lines[3]);                                // never starts like a formula
        Assert.Contains("\"Kiosk; \"\"róg\"\"\"", lines[4]);               // separator and quotes survive
    }

    [Fact]
    public void Csv_CanUseDotsAndCommasForOtherPrograms()
    {
        var text = ExportService.TransactionsToCsvText(Data(), Cats, [Holiday], new CsvOptions { Separator = ',', DecimalComma = false });
        var line = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.StartsWith("2026-07-01,Wydatek,425.12,EUR,100.00,4.2512,", line);
    }

    [Fact]
    public void Csv_File_StartsWithAByteOrderMark_SoPolishLettersSurviveInExcel()
    {
        var path = Path.Combine(_dir, "t.csv");
        ExportService.TransactionsToCsv(path, Data(), Cats, [Holiday]);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("Przychód", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ReportXlsx_ContainsSummaryComparisonCategoriesAndTrend()
    {
        var all = new List<Transaction>(Data())
        {
            Make.Tx("2026-06-10", TransactionType.Expense, 100m, Cat("Spożywcze")),
            Make.Tx("2026-06-11", TransactionType.Income, 2000m, Cat("Wynagrodzenie")),
        };
        var path = Path.Combine(_dir, "r.xlsx");
        ExportService.ReportToXlsx(path, ReportScope.Month, new DateOnly(2026, 7, 15), all, Cats);

        var sheets = WorkbookLoader.Load(path).ToDictionary(s => s.Name);
        Assert.Equal(["Podsumowanie", "Kategorie", "Trend"], sheets.Keys);

        var sum = sheets["Podsumowanie"];
        Assert.Equal("2026-07", sum.Text(0, 1));
        Assert.Equal("Przychody", sum.At(3, 0));
        Assert.Equal(1000.0, sum.At(3, 1));          // July income
        Assert.Equal(2000.0, sum.At(3, 2));          // June income
        Assert.Equal(484.42, (double)Math.Round((decimal)(double)sum.At(4, 1)!, 2));   // July expenses 54.30 + 425.12 + 5.00

        var trend = sheets["Trend"];
        Assert.Equal(13, trend.RowCount);            // header + 12 months
        Assert.Equal("2026-07", trend.At(12, 0));

        var categories = sheets["Kategorie"];
        Assert.Contains(Enumerable.Range(1, categories.RowCount - 1), r => categories.Text(r, 0) == "Spożywcze");
    }

    [Fact]
    public void YearReport_ListsAllTwelveMonthsOfThatYear()
    {
        var path = Path.Combine(_dir, "y.xlsx");
        ExportService.ReportToXlsx(path, ReportScope.Year, new DateOnly(2026, 3, 1), Data(), Cats);
        var trend = WorkbookLoader.Load(path).Single(s => s.Name == "Trend");
        Assert.Equal("2026-01", trend.At(1, 0));
        Assert.Equal("2026-12", trend.At(12, 0));
    }
}
