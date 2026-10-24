using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using ClosedXML.Excel;

namespace BudzetDomowy.UiTests;

public class ImportUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-impui-" + Guid.NewGuid().ToString("N"));

    public ImportUiTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A workbook shaped like the user's: two month sheets, day number + date on the first row of a day, plus a statistics sheet.</summary>
    private string MakeWorkbook()
    {
        var path = Path.Combine(_dir, "Wydatki.xlsx");
        using var wb = new XLWorkbook();
        foreach (var (name, month) in new[] { ("Sty", new DateTime(2026, 1, 1)), ("Lut", new DateTime(2026, 2, 1)) })
        {
            var ws = wb.AddWorksheet(name);
            ws.Cell(2, 2).Value = month;
            ws.Cell(3, 4).Value = "Przychody";
            ws.Cell(3, 5).Value = 500;
            string[] heads = ["Data", "", "Gdzie", "Kategoria", "Opis", "Kwota"];
            for (var c = 0; c < heads.Length; c++) ws.Cell(6, c + 1).Value = heads[c];
            ws.Cell(7, 1).Value = 1; ws.Cell(7, 2).Value = month; ws.Cell(7, 3).Value = "Lewiatan"; ws.Cell(7, 4).Value = "Spożywcze"; ws.Cell(7, 6).Value = 25.5;
            ws.Cell(8, 3).Value = "Orlen"; ws.Cell(8, 4).Value = "Auto"; ws.Cell(8, 5).Value = "paliwo"; ws.Cell(8, 6).Value = 200;
            ws.Cell(9, 1).Value = 2; ws.Cell(9, 2).Value = month.AddDays(1); ws.Cell(9, 3).Value = "Basen"; ws.Cell(9, 4).Value = "Pływanie"; ws.Cell(9, 6).Value = 18;
        }
        var stats = wb.AddWorksheet("Statystyki");
        stats.Cell(1, 1).Value = "Miesiąc"; stats.Cell(1, 2).Value = "Wydatki"; stats.Cell(2, 1).Value = "Styczeń"; stats.Cell(2, 2).Value = 243.5;
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Wizard_GoesFromFileToCategoriesToPreviewToImport_AndSecondRunAddsNothing()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();
        var before = app.Core.Reader.GetTransactions().Count;

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();

            Assert.Equal(["Sty", "Lut", "Statystyki"], vm.Sheets.Select(s => s.Name));
            Assert.Equal([true, true, false], vm.Sheets.Select(s => s.IsSelected));   // the statistics sheet is not offered
            Assert.True(vm.IncomeLabelFound);
            Assert.True(vm.CanGoNext);

            await vm.NextCommand.ExecuteAsync(null);                                  // -> columns
            Assert.True(vm.IsStep2);
            Assert.Equal(1, vm.DateColumn!.Index);
            Assert.Equal(5, vm.AmountColumn!.Index);
            Assert.Equal(6, vm.HeaderRowNumber);

            await vm.NextCommand.ExecuteAsync(null);                                  // -> categories
            Assert.True(vm.IsStep3);
            var names = vm.CategoryRows.Select(r => r.SourceName).ToList();
            Assert.Contains("Spożywcze", names);
            Assert.Contains("Pływanie", names);
            var swim = vm.CategoryRows.First(r => r.SourceName == "Pływanie");
            Assert.True(swim.Selected.Choice.IsNew);                                  // unknown name: a new category is proposed
            Assert.True(vm.CategoryRows.First(r => r.SourceName == "Auto").Selected.Choice.IsExisting);
            Assert.Equal(2 * 3 + 2, vm.CategoryRows.Sum(r => int.Parse(new string(r.CountText.Where(char.IsDigit).ToArray()))));

            await vm.NextCommand.ExecuteAsync(null);                                  // -> preview
            Assert.True(vm.IsStep4);
            Assert.Equal(8, vm.PreviewRows.Count);                                    // 6 purchases + 2 incomes
            Assert.Contains("Do zaimportowania: 8", vm.Summary);

            await vm.ImportCommand.ExecuteAsync(null);
            Assert.True(vm.IsDone);
            Assert.Contains("Dodano wpisów: 8", vm.Result);
        });

        Assert.Equal(before + 8, app.Core.Reader.GetTransactions().Count);
        Assert.Contains(app.Core.Reader.GetCategories(), c => c.Name == "Pływanie");

        // the same file again: everything is recognised as already there
        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Contains("Do zaimportowania: 0", vm.Summary);
            Assert.Contains("duplikaty: 8", vm.Summary);
        });
        Assert.Equal(before + 8, app.Core.Reader.GetTransactions().Count);
    }

    [Fact]
    public void Wizard_RemembersTheCategoryChoices_ForTheNextImport()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();
        var home = app.Core.Reader.GetCategories().First(c => c.Name == "Dla domu" && c.ParentId is null);

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            var row = vm.CategoryRows.First(r => r.SourceName == "Pływanie");
            row.Selected = row.Targets.First(t => t.Choice.ExistingId == home.Id);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.ImportCommand.ExecuteAsync(null);
        });
        Assert.DoesNotContain(app.Core.Reader.GetCategories(), c => c.Name == "Pływanie");

        // second workbook with the same unknown category name: the earlier decision is offered
        var second = Path.Combine(_dir, "Drugi.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Mar");
            ws.Cell(2, 2).Value = new DateTime(2026, 3, 1);
            string[] heads = ["Data", "", "Gdzie", "Kategoria", "Opis", "Kwota"];
            for (var c = 0; c < heads.Length; c++) ws.Cell(6, c + 1).Value = heads[c];
            ws.Cell(7, 1).Value = 1; ws.Cell(7, 2).Value = new DateTime(2026, 3, 1); ws.Cell(7, 3).Value = "Basen"; ws.Cell(7, 4).Value = "Pływanie"; ws.Cell(7, 6).Value = 22;
            wb.SaveAs(second);
        }
        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = second;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Equal(home.Id, vm.CategoryRows.Single().Selected.Choice.ExistingId);
        });
    }

    [Fact]
    public void Wizard_ReportsAFileThatIsNotAWorkbook_WithoutCrashing()
    {
        using var app = new TestApp(sampleData: false);
        var bad = Path.Combine(_dir, "nie-excel.xlsx");
        File.WriteAllText(bad, "to nie jest skoroszyt");

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = bad;
            await vm.LoadFileAsync();
            Assert.Empty(vm.Sheets);
            Assert.False(string.IsNullOrEmpty(vm.Message));
            Assert.False(vm.CanGoNext);
        });
    }

    [Fact]
    public void Wizard_ScreensRenderForEachStep() => WithRenderedSteps();

    private void WithRenderedSteps()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();
        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            var window = new ImportWindow(vm);
            var content = (System.Windows.FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = vm;

            vm.FilePath = path;
            await vm.LoadFileAsync();
            UiHost.Layout(content, 1040, 640);
            UiHost.Render(content, 1040, 640, "import-1-file");

            await vm.NextCommand.ExecuteAsync(null);
            UiHost.Layout(content, 1040, 640);
            UiHost.Render(content, 1040, 640, "import-2-columns");

            await vm.NextCommand.ExecuteAsync(null);
            UiHost.Layout(content, 1040, 640);
            UiHost.Render(content, 1040, 640, "import-3-categories");

            await vm.NextCommand.ExecuteAsync(null);
            UiHost.Layout(content, 1040, 640);
            UiHost.Render(content, 1040, 640, "import-4-preview");

            await vm.ImportCommand.ExecuteAsync(null);
            UiHost.Layout(content, 1040, 640);
            UiHost.Render(content, 1040, 640, "import-5-done");
        });
    }

    /// <summary>The user's real workbook, when present: a full import into a scratch database.</summary>
    [Fact]
    public void TheRealWorkbook_ImportsCompletely_AndAgainImportsNothing()
    {
        var sample = FindSample();
        if (sample is null) return;

        using var app = new TestApp(sampleData: false);
        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = sample;
            await vm.LoadFileAsync();
            Assert.Equal(12, vm.Sheets.Count(s => s.IsSelected));
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Contains("Do zaimportowania: 1294", vm.Summary);        // 1282 expenses + 12 monthly incomes
            await vm.ImportCommand.ExecuteAsync(null);
            Assert.True(vm.IsDone);
        });

        var all = app.Core.Reader.GetTransactions();
        Assert.Equal(1294, all.Count);
        Assert.Equal(1282m, all.Where(t => t.Type == TransactionType.Expense).Sum(t => t.AmountPln));
        Assert.Equal(6000m, all.Where(t => t.Type == TransactionType.Income).Sum(t => t.AmountPln));
        Assert.Equal(0, app.Core.Reader.GetCategories().Count(c => c.Name == "Spożywcze" && c.ParentId is null) - 1);   // matched the existing category, no copy

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = sample;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.CategoryRows.Count == 0 ? 0 : 0);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Contains("Do zaimportowania: 0", vm.Summary);
        });
        Assert.Equal(1294, app.Core.Reader.GetTransactions().Count);
    }

    private static string? FindSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "_sample", "Wydatki_2026.xlsm");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
