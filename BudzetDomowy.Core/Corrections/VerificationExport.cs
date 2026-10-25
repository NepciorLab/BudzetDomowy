using ClosedXML.Excel;

namespace BudzetDomowy.Core.Corrections;

/// <summary>Writes the check of a correction to an .xlsx file: a summary and every day that was expected to change or did change.</summary>
public static class VerificationExport
{
    private const string MoneyFormat = "#,##0.00;[Red]-#,##0.00";

    public static void Write(string path, string title, VerificationReport report)
    {
        using var wb = new XLWorkbook();

        var summary = wb.AddWorksheet("Podsumowanie");
        summary.Cell(1, 1).Value = title;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(2, 1).Value = report.AllOk ? "Wynik kontroli: wszystko się zgadza." : $"Wynik kontroli: NIEZGODNOŚCI w dniach: {report.Problems.Count}.";
        summary.Cell(2, 1).Style.Font.Bold = true;
        summary.Cell(3, 1).Value = "Sprawdzonych dni (z danymi):";
        summary.Cell(3, 2).Value = report.DaysChecked;
        summary.Cell(4, 1).Value = "Dni ze zmianą lub zapowiedzią zmiany:";
        summary.Cell(4, 2).Value = report.ChangedDays;

        string[] heads = ["", "Przychody", "Wydatki", "Wpisy"];
        for (var c = 0; c < heads.Length; c++) summary.Cell(6, c + 1).Value = heads[c];
        summary.Range(6, 1, 6, 4).Style.Font.Bold = true;
        Row(summary, 7, "Przed", report.TotalBefore);
        Row(summary, 8, "Po", report.TotalAfter);
        Row(summary, 9, "Zmiana rzeczywista", report.TotalAfter - report.TotalBefore);
        Row(summary, 10, "Zmiana zapowiedziana", report.TotalExpected);
        summary.Columns().AdjustToContents();

        var days = wb.AddWorksheet("Dni");
        string[] dayHeads =
        [
            "Data", "Przychody przed", "Przychody po", "Zmiana przychodów", "Zapowiedź", "Wydatki przed", "Wydatki po", "Zmiana wydatków", "Zapowiedź",
            "Wpisy przed", "Wpisy po", "Stan",
        ];
        for (var c = 0; c < dayHeads.Length; c++) days.Cell(1, c + 1).Value = dayHeads[c];
        days.Range(1, 1, 1, dayHeads.Length).Style.Font.Bold = true;

        var row = 2;
        foreach (var d in report.Days)
        {
            days.Cell(row, 1).Value = d.Date.ToDateTime(TimeOnly.MinValue);
            days.Cell(row, 1).Style.DateFormat.Format = "yyyy-MM-dd";
            days.Cell(row, 2).Value = d.Before.Income;
            days.Cell(row, 3).Value = d.After.Income;
            days.Cell(row, 4).Value = d.Actual.Income;
            days.Cell(row, 5).Value = d.Expected.Income;
            days.Cell(row, 6).Value = d.Before.Expense;
            days.Cell(row, 7).Value = d.After.Expense;
            days.Cell(row, 8).Value = d.Actual.Expense;
            days.Cell(row, 9).Value = d.Expected.Expense;
            days.Cell(row, 10).Value = d.Before.Count;
            days.Cell(row, 11).Value = d.After.Count;
            days.Cell(row, 12).Value = d.Ok ? "OK" : "NIEZGODNE";
            if (!d.Ok) days.Range(row, 1, row, dayHeads.Length).Style.Fill.BackgroundColor = XLColor.LightSalmon;
            days.Range(row, 2, row, 9).Style.NumberFormat.Format = MoneyFormat;
            row++;
        }
        days.SheetView.FreezeRows(1);
        days.Columns().AdjustToContents();

        wb.SaveAs(path);
    }

    private static void Row(IXLWorksheet sheet, int row, string label, DaySum sum)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Cell(row, 2).Value = sum.Income;
        sheet.Cell(row, 3).Value = sum.Expense;
        sheet.Cell(row, 4).Value = sum.Count;
        sheet.Range(row, 2, row, 3).Style.NumberFormat.Format = MoneyFormat;
    }
}
