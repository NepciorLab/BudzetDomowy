using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using ClosedXML.Excel;

namespace BudzetDomowy.UiTests;

/// <summary>The note on the bar during long operations, correcting rows of the import, the income cell, the entries behind a place.</summary>
public sealed class V9UiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budzet-v9-" + Guid.NewGuid().ToString("N"));

    public V9UiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    // ----- the note on the bar -----

    /// <summary>Runs the long operations on the calling thread and records what the bar showed while the work was running.</summary>
    private sealed class BusyProbe : IDisposable
    {
        private readonly BusyStatus _busy = BusyStatus.Instance;
        private readonly int _threshold = BusyStatus.BulkThreshold;
        private readonly Func<Action, Task> _executor;

        public List<(bool IsBusy, string Text)> Seen { get; } = [];

        public BusyProbe(int threshold)
        {
            _executor = _busy.Executor;
            BusyStatus.BulkThreshold = threshold;
            _busy.Executor = work =>
            {
                Seen.Add((_busy.IsBusy, _busy.Text));
                work();
                return Task.CompletedTask;
            };
        }

        public void Dispose()
        {
            BusyStatus.BulkThreshold = _threshold;
            _busy.Executor = _executor;
        }
    }

    private static TransactionsViewModel AllTransactions(TestApp app)
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("Range");
        vm.From = DateTime.Today.AddYears(-2);
        vm.To = DateTime.Today.AddDays(1);
        vm.Reload();
        return vm;
    }

    [Fact]
    public void BigDelete_ShowsWhatItDoesOnTheBar_AndNothingStaysBehind() => WithApp(app =>
    {
        var vm = AllTransactions(app);
        using var probe = new BusyProbe(threshold: 5);
        var chosen = vm.Rows.Take(6).ToList();
        Assert.Equal(6, chosen.Count);
        var before = app.Core.Reader.GetTransactions().Count;

        vm.SetSelection(chosen);
        vm.DeleteSelected();

        Assert.Equal([(true, "Usuwanie wpisów: 6…")], probe.Seen);                          // the note was up while the work ran
        Assert.False(BusyStatus.Instance.IsBusy);                                           // and is gone afterwards
        Assert.Equal("", BusyStatus.Instance.Text);
        Assert.True(vm.Running.IsCompletedSuccessfully);
        Assert.Equal(before - 6, app.Core.Reader.GetTransactions().Count);
    });

    [Fact]
    public void ASmallDelete_NeedsNoNote_AndHappensAtOnce() => WithApp(app =>
    {
        var vm = AllTransactions(app);
        using var probe = new BusyProbe(threshold: 5);
        var before = app.Core.Reader.GetTransactions().Count;

        vm.SetSelection(vm.Rows.Take(2).ToList());
        vm.DeleteSelected();

        Assert.Empty(probe.Seen);
        Assert.Equal(before - 2, app.Core.Reader.GetTransactions().Count);
    });

    [Fact]
    public void BigCategoryChange_ShowsANote_AndChangesAllOfThem() => WithApp(app =>
    {
        var vm = AllTransactions(app);
        using var probe = new BusyProbe(threshold: 5);
        var target = app.Core.Reader.GetCategories().First(c => c.Name == "Rachunki").Id;
        var chosen = vm.Rows.Where(r => !r.IsIncome).Take(7).ToList();
        var ids = chosen.Select(r => r.Id).ToList();

        vm.SetSelection(chosen);
        vm.ChangeCategoryOfSelected(target);

        Assert.Equal([(true, "Zmiana kategorii wpisów: 7…")], probe.Seen);
        Assert.All(app.Core.Reader.GetTransactions().Where(t => ids.Contains(t.Id)), t => Assert.Equal(target, t.CategoryId));
        Assert.False(BusyStatus.Instance.IsBusy);
    });

    [Fact]
    public void TheBar_HasANoteNextToTheCalculatorButton_ThatFollowsTheStatus() => WithApp(app =>
    {
        var window = new MainWindow(app.Get<MainViewModel>(), app.Get<SettingsStore>());
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 1280, 800);
        var bar = Assert.IsType<StackPanel>(window.FindName("BusyBar"));
        var label = Assert.IsType<TextBlock>(window.FindName("BusyText"));
        var row = (Panel)((FrameworkElement)window.FindName("CalculatorButton")!).Parent;
        Assert.Contains(bar, row.Children.Cast<UIElement>());                                // on the same bar as the buttons
        Assert.Equal(Visibility.Collapsed, bar.Visibility);

        var busy = BusyStatus.Instance;
        var seen = new List<(Visibility Bar, string Text)>();
        var executor = busy.Executor;
        busy.Executor = work =>
        {
            UiHost.Layout(content, 1280, 800);
            seen.Add((bar.Visibility, label.Text));
            work();
            return Task.CompletedTask;
        };
        try
        {
            busy.RunAsync("Usuwanie wpisów: 1200…", () => { }).Wait();
        }
        finally
        {
            busy.Executor = executor;
        }

        Assert.Equal([(Visibility.Visible, "Usuwanie wpisów: 1200…")], seen);
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void TheScreensAreRefreshedOnceWhenALongOperationIsOver_NotForEveryChange() => WithApp(app =>
    {
        var hub = app.Get<DataHub>();
        BusyStatus.Instance.Attach(hub);
        var refreshed = 0;
        hub.Changed += (_, _) => refreshed++;

        static void Wait(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!done() && DateTime.UtcNow < until)
            {
                UiHost.Pump();
                Thread.Sleep(20);
            }
        }

        using (hub.Hold())
        {
            for (var i = 0; i < 5; i++) app.Core.Transactions.Add(DateOnly.FromDateTime(DateTime.Today), TransactionType.Expense, 1m + i, null, "x" + i, null);
            Thread.Sleep(300);
            UiHost.Pump();
            Assert.Equal(0, refreshed);                                                      // held back while the work goes on
        }

        Wait(() => refreshed > 0);
        Thread.Sleep(300);
        UiHost.Pump();
        Assert.Equal(1, refreshed);                                                          // one refresh for all of the changes

        app.Core.Transactions.Add(DateOnly.FromDateTime(DateTime.Today), TransactionType.Expense, 9m, null, "later", null);
        Wait(() => refreshed > 1);
        Assert.Equal(2, refreshed);                                                          // and normal refreshing continues afterwards
    });

    // ----- import: correcting rows and the income cell -----

    /// <summary>One month sheet: a row without a readable amount, a normal one, and the income in a cell with no label next to it.</summary>
    private string MakeWorkbook()
    {
        var path = Path.Combine(_dir, "Wydatki.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sty");
        var month = new DateTime(2026, 1, 1);
        ws.Cell(2, 2).Value = month;
        ws.Cell(3, 8).Value = 750;                                                           // H3: the income, with no "Przychody" label
        string[] heads = ["Data", "", "Gdzie", "Kategoria", "Opis", "Kwota"];
        for (var c = 0; c < heads.Length; c++) ws.Cell(6, c + 1).Value = heads[c];
        ws.Cell(7, 1).Value = 1; ws.Cell(7, 2).Value = month; ws.Cell(7, 3).Value = "Lewiatan"; ws.Cell(7, 4).Value = "Spożywcze"; ws.Cell(7, 6).Value = "12,3x";
        ws.Cell(8, 3).Value = "Orlen"; ws.Cell(8, 4).Value = "Auto"; ws.Cell(8, 5).Value = "paliwo"; ws.Cell(8, 6).Value = 200;
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Import_AnAddressOfTheIncomeCellCanBeGiven_WhenTheLabelIsNotFound()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();

            Assert.False(vm.IncomeLabelFound);
            Assert.Contains("Nie znaleziono napisu „Przychody”", vm.IncomeCellHint);        // it says where to look next

            vm.IncomeCell = "h3";
            Assert.Equal("Arkusz „Sty”, komórka H3: 750", vm.IncomeCellHint);               // and what the address points at
            vm.IncomeCell = "H9";
            Assert.Equal("Arkusz „Sty”, komórka H9: pusta.", vm.IncomeCellHint);
            vm.IncomeCell = "to nie adres";
            Assert.Equal("To nie jest adres komórki (przykład: E3).", vm.IncomeCellHint);

            vm.IncomeCell = "H3";
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.True(vm.IsStep4);

            var income = vm.PreviewRows.Single(r => r.IsIncome);
            Assert.Equal(("Sty", 3), (income.Sheet, income.ExcelRow));
            Assert.Equal("+750,00", income.Amount);
            await vm.ImportCommand.ExecuteAsync(null);
        });

        Assert.Equal(750m, app.Core.Reader.GetTransactions().Single(t => t.Type == TransactionType.Income).AmountPln);
    }

    [Fact]
    public void Import_WithoutTheAddress_NoIncomeIsFound_AndTheHintExplainsWhy()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.PreviewRows, r => r.IsIncome);
        });
    }

    [Fact]
    public void Import_ARowThatCouldNotBeReadCanBeCorrectedInThePreview_AndIsImportedThen()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();
        var editor = app.Get<FakeImportRowEditor>();

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();
            vm.IncomeCell = "H3";
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);

            var bad = vm.PreviewRows.Single(r => r.Kind == "Error");
            Assert.Equal(("Lewiatan", 7, true), (bad.Place, bad.ExcelRow, bad.CanFix));
            Assert.Equal("błąd: nie można odczytać kwoty", bad.Status);
            Assert.Contains("błędy: 1", vm.Summary);                                         // the summary counts it

            var fine = vm.PreviewRows.First(r => r.Kind == "New" && !r.IsIncome);
            vm.SelectedPreviewRow = fine;
            Assert.False(vm.FixRowCommand.CanExecute(null));                                // a row that is fine is not offered for correcting

            vm.SelectedPreviewRow = bad;
            Assert.True(vm.FixRowCommand.CanExecute(null));
            editor.Answers.Enqueue(null);                                                    // cancelled: nothing changes
            vm.FixRowCommand.Execute(null);
            Assert.Equal("Error", vm.PreviewRows.Single(r => r.ExcelRow == 7).Kind);
            Assert.Contains("błędy: 1", vm.Summary);

            var asked = Assert.Single(editor.Asked);
            Assert.Equal("12,3x", asked.Row.RawAmount);                                      // the window gets what the cell held
            Assert.Equal("błąd: nie można odczytać kwoty", asked.Problem);
            Assert.True(asked.Categories > 5);

            var food = app.Core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;
            editor.Answers.Enqueue(new ImportRowFix(new DateOnly(2026, 1, 1), 12.30m, TransactionType.Expense, "Lewiatan", "", food, false));
            vm.SelectedPreviewRow = vm.PreviewRows.Single(r => r.ExcelRow == 7);
            vm.FixRowCommand.Execute(null);

            var corrected = vm.PreviewRows.Single(r => r.ExcelRow == 7);
            Assert.Equal(("New", "poprawiono", "−12,30"), (corrected.Kind, corrected.Status, corrected.Amount));
            Assert.Equal("Spożywcze", corrected.Category);                                   // the category chosen in the window
            Assert.True(corrected.CanFix);                                                   // it can be corrected again
            vm.Filter = vm.Filters.Single(f => f.Key == "problem");
            Assert.Empty(vm.PreviewRows);                                                    // no problems are left
            Assert.Contains("błędy: 0", vm.Summary);

            await vm.ImportCommand.ExecuteAsync(null);
        });

        var imported = app.Core.Reader.GetTransactions().Single(t => t.Place == "Lewiatan");
        Assert.Equal((12.30m, new DateOnly(2026, 1, 1)), (imported.AmountPln, imported.Date));
        Assert.Equal("Spożywcze", app.Core.Reader.GetCategories().First(c => c.Id == imported.CategoryId).Name);
    }

    [Fact]
    public void Import_ARowCanBeLeftOut_FromTheWindowOfCorrection()
    {
        using var app = new TestApp(sampleData: false);
        var path = MakeWorkbook();
        var editor = app.Get<FakeImportRowEditor>();

        UiHost.RunAsync(async () =>
        {
            var vm = app.Get<ImportViewModel>();
            vm.FilePath = path;
            await vm.LoadFileAsync();
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);

            vm.SelectedPreviewRow = vm.PreviewRows.Single(r => r.Kind == "Error");
            editor.Answers.Enqueue(new ImportRowFix(null, 0, TransactionType.Expense, "Lewiatan", "", null, Skip: true));
            vm.FixRowCommand.Execute(null);

            var left = vm.PreviewRows.Single(r => r.ExcelRow == 7);
            Assert.Equal(("Skipped", "pominięto na Twoje polecenie"), (left.Kind, left.Status));
            await vm.ImportCommand.ExecuteAsync(null);
        });

        Assert.DoesNotContain(app.Core.Reader.GetTransactions(), t => t.Place == "Lewiatan");
        Assert.Contains(app.Core.Reader.GetTransactions(), t => t.Place == "Orlen");
    }

    // ----- the window of correction -----

    private static ImportRow BadRow(string raw = "12,3x", string message = "bad-amount") =>
        new("Sty", 7, new DateOnly(2026, 1, 1), 0, TransactionType.Expense, "Lewiatan", "zakupy", "Spożywcze", ImportRowStatus.Error, message) { RawAmount = raw };

    [Fact]
    public void RowWindow_StartsFromTheRow_AndDoesNotPretendAnUnreadAmountIsZloty() => WithApp(app =>
    {
        var categories = CategoryHelpers.Flatten(app.Core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
        var vm = new ImportRowEditViewModel(BadRow(), categories, "błąd: nie można odczytać kwoty");

        Assert.Equal("Arkusz „Sty”, wiersz 7", vm.Heading);
        Assert.Equal("W pliku w polu kwoty było: „12,3x”", vm.Original);
        Assert.Equal("Kategoria w pliku: „Spożywcze”", vm.FileCategory);
        Assert.Equal((new DateTime(2026, 1, 1), "", "Lewiatan", "zakupy", false), (vm.Date, vm.AmountText, vm.Place, vm.Note, vm.IsIncome));

        var foreign = new ImportRowEditViewModel(BadRow("12 EUR", "foreign-currency") with { Amount = 12 }, categories, "x");
        Assert.Equal("", foreign.AmountText);                                                // 12 EUR is not 12 zł
        var negative = new ImportRowEditViewModel(BadRow("-20", "negative") with { Amount = 20 }, categories, "x");
        Assert.Equal("20", negative.AmountText);                                              // a plain amount is offered as it is
    });

    [Fact]
    public void RowWindow_ChecksTheDateAndAmount_AndGivesBackTheCorrection() => WithApp(app =>
    {
        var categories = CategoryHelpers.Flatten(app.Core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
        var vm = new ImportRowEditViewModel(BadRow() with { Date = null, Message = "no-date" }, categories, "błąd: brak daty");
        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;

        vm.SaveCommand.Execute(null);
        Assert.Equal("Podaj datę.", vm.ErrorMessage);
        vm.Date = new DateTime(2026, 2, 3);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Podaj kwotę większą od zera (liczbę albo działanie).", vm.ErrorMessage);
        vm.AmountText = "0";
        vm.SaveCommand.Execute(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(closed);

        var food = categories.First(c => c.Category.Name == "Spożywcze" && c.Category.ParentId is null).Category.Id;
        vm.AmountText = "2*6,15";                                                            // a calculation works as in the entry window
        vm.SelectedCategory = vm.Categories.First(c => c.Choice.ExistingId == food);
        vm.Note = "inne zakupy";
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(new ImportRowFix(new DateOnly(2026, 2, 3), 12.30m, TransactionType.Expense, "Lewiatan", "inne zakupy", food, false), vm.Result);
    });

    [Fact]
    public void RowWindow_TheTypeDecidesWhichCategoriesAreOffered_AndSkipOrCancelGiveNoCorrection() => WithApp(app =>
    {
        var categories = CategoryHelpers.Flatten(app.Core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
        var vm = new ImportRowEditViewModel(BadRow("-5", "negative") with { Amount = 5 }, categories, "x");
        var food = vm.Categories.First(c => c.Choice.ExistingId is not null && c.Label == "Spożywcze");
        vm.SelectedCategory = food;
        Assert.Contains(food, vm.Categories);

        vm.IsIncome = true;                                                                  // an income is not filed under groceries
        Assert.DoesNotContain(vm.Categories, c => c.Choice.ExistingId == food.Choice.ExistingId);
        Assert.Equal(vm.Categories[0], vm.SelectedCategory);                                 // back to "as in the file"
        Assert.Contains(vm.Categories, c => c.Label.StartsWith("Wynagrodzenie") || c.Label.StartsWith("Inne przychody"));

        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;
        vm.SkipCommand.Execute(null);
        Assert.True(vm.Result!.Skip);
        Assert.True(closed);

        var other = new ImportRowEditViewModel(BadRow(), categories, "x");
        bool? otherClosed = null;
        other.CloseRequested += ok => otherClosed = ok;
        other.CancelCommand.Execute(null);
        Assert.Equal((false, null), (otherClosed, other.Result));
    });

    [Fact]
    public void RowWindow_And_PreviewButton_Render() => WithApp(app =>
    {
        var categories = CategoryHelpers.Flatten(app.Core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
        var vm = new ImportRowEditViewModel(BadRow(), categories, "błąd: nie można odczytać kwoty");
        var window = new ImportRowWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 560, 560);
        var texts = UiHost.Texts(content);
        var buttons = UiHost.Descendants<Button>(content).Select(b => b.Content?.ToString()).ToList();
        window.Content = null;
        content.DataContext = vm;
        UiHost.Render(content, 560, 560, "import-row");

        Assert.Contains("Popraw wiersz importu", texts);
        Assert.Contains("błąd: nie można odczytać kwoty", texts);
        Assert.Contains("W pliku w polu kwoty było: „12,3x”", texts);
        Assert.Contains("Zapisz poprawkę", buttons);
        Assert.Contains("Pomiń ten wiersz", buttons);

        var import = new ImportWindow(app.Get<ImportViewModel>());
        Assert.NotNull(import.FindName("FixButton"));
        Assert.NotNull(import.FindName("IncomeCellBox"));
    });

    // ----- places: the entries behind a total -----

    private static void AddTx(TestApp app, string date, string place, decimal amount, string note, string? category = null, TransactionType type = TransactionType.Expense)
    {
        Guid? categoryId = category is null ? null : app.Core.Reader.GetCategories().First(c => c.Name == category).Id;
        app.Core.Transactions.Add(DateOnly.Parse(date), type, amount, categoryId, note, null, place: place);
    }

    [Fact]
    public void Places_SelectingAPlaceShowsTheEntriesCountedInItsTotal() => WithApp(app =>
    {
        foreach (var t in app.Core.Reader.GetTransactions()) app.Core.Transactions.Delete([t]);
        AddTx(app, "2026-03-01", "Biedronka", 10, "chleb", "Spożywcze");
        AddTx(app, "2026-03-05", "biedronka ", 20.5m, "mleko", "Spożywcze");
        AddTx(app, "2026-03-09", "BIEDRONKA", 5, "bez kategorii");
        AddTx(app, "2026-02-27", "Biedronka", 99, "luty");
        AddTx(app, "2026-03-10", "Biedronka", 7, "zwrot", null, TransactionType.Income);
        AddTx(app, "2026-03-02", "Orlen", 40, "paliwo", "Auto");

        var vm = new PlacesViewModel(app.Core);
        vm.SetPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        Assert.Equal(["Orlen", "Biedronka"], vm.Rows.Select(r => r.Name));                    // biggest total first
        Assert.False(vm.HasDetails);                                                         // nothing selected: nothing to show yet
        Assert.Empty(vm.Details);

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Biedronka");

        Assert.True(vm.HasDetails);
        Assert.Equal("Wpisy: Biedronka (3)", vm.DetailsTitle);
        Assert.Equal([new DateTime(2026, 3, 9), new DateTime(2026, 3, 5), new DateTime(2026, 3, 1)], vm.Details.Select(d => d.Date));
        Assert.Equal(vm.SelectedRow!.Amount, vm.Details.Sum(d => d.Amount));                       // the same entries as in the total
        Assert.Equal(["bez kategorii", "mleko", "chleb"], vm.Details.Select(d => d.Note));
        Assert.Equal(["(bez kategorii)", "Spożywcze", "Spożywcze"], vm.Details.Select(d => d.Category).Select(c => c.Contains('›') ? c.Split('›').Last().Trim() : c));

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Orlen");
        Assert.Equal(("Wpisy: Orlen (1)", 1), (vm.DetailsTitle, vm.Details.Count));

        vm.SelectedRow = null;
        Assert.Empty(vm.Details);
        Assert.False(vm.HasDetails);
    });

    [Fact]
    public void Places_TheSelectionFollowsChangesOfThePeriodTypeAndSearch() => WithApp(app =>
    {
        foreach (var t in app.Core.Reader.GetTransactions()) app.Core.Transactions.Delete([t]);
        AddTx(app, "2026-03-01", "Biedronka", 10, "a");
        AddTx(app, "2026-04-01", "Biedronka", 30, "b");
        AddTx(app, "2026-03-02", "Orlen", 40, "c");
        AddTx(app, "2026-03-03", "Zwrot sklepu", 5, "d", null, TransactionType.Income);

        var vm = new PlacesViewModel(app.Core);
        vm.SetPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Biedronka");
        Assert.Equal(["a"], vm.Details.Select(d => d.Note));

        vm.SetPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30));                    // a longer period: the same place, more entries
        Assert.Equal("Biedronka", vm.SelectedRow!.Name);
        Assert.Equal(["b", "a"], vm.Details.Select(d => d.Note));

        vm.Search = "orl";                                                                    // the place is filtered out of the list
        Assert.Null(vm.SelectedRow);
        Assert.Empty(vm.Details);

        vm.Search = "";
        vm.SetTypeCommand.Execute("Income");
        Assert.Equal(["Zwrot sklepu"], vm.Rows.Select(r => r.Name));
        vm.SelectedRow = vm.Rows[0];
        Assert.Equal(["d"], vm.Details.Select(d => d.Note));
    });

    [Fact]
    public void Places_TheEntriesAreShownBesideTheTotals_ForLookingOnly() => WithApp(app =>
    {
        foreach (var t in app.Core.Reader.GetTransactions()) app.Core.Transactions.Delete([t]);
        AddTx(app, "2026-03-01", "Biedronka", 10, "chleb", "Spożywcze");
        AddTx(app, "2026-03-05", "Biedronka", 20.5m, "mleko", "Spożywcze");
        AddTx(app, "2026-03-02", "Orlen", 40, "paliwo", "Auto");

        var reports = app.Get<ReportsViewModel>();
        reports.Tab = ReportTab.Places;
        reports.Places.SetPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var view = new ReportsView { DataContext = reports };
        UiHost.Layout(view, 1400, 760);
        Assert.Contains("Wybierz miejsce z listy, aby zobaczyć wpisy, z których powstała jego suma.", UiHost.Texts(view));

        reports.Places.SelectedRow = reports.Places.Rows.Single(r => r.Name == "Biedronka");
        UiHost.Layout(view, 1400, 760);
        UiHost.Render(view, 1400, 760, "places-details");

        var grids = UiHost.Descendants<DataGrid>(view).Where(UiHost.IsShown).ToList();
        var details = Assert.Single(grids, g => g.Name == "PlaceDetailsGrid");
        var totals = grids.Single(g => g != details && g.Items.Count > 0 && g.Items[0] is PlaceRow);
        Assert.True(details.IsReadOnly);
        Assert.False(details.CanUserAddRows);
        Assert.Equal(["Data", "Kwota", "Kategoria", "Notatka"], details.Columns.Select(c => c.Header?.ToString()));      // only these four
        Assert.Equal(2, details.Items.Count);
        Assert.All(details.Columns, c => Assert.True(c.IsReadOnly));

        var texts = UiHost.Texts(view);
        Assert.Contains("Wpisy: Biedronka (2)", texts);
        Assert.Equal(["mleko", "chleb"], details.Items.Cast<PlaceTransactionRow>().Select(d => d.Note));      // newest first
        Assert.DoesNotContain("paliwo", texts);                                              // the other place's entries are not mixed in

        // beside, to the right of, the list of places
        var totalsLeft = totals.TranslatePoint(new Point(0, 0), view).X;
        var detailsLeft = details.TranslatePoint(new Point(0, 0), view).X;
        Assert.True(detailsLeft > totalsLeft + totals.ActualWidth - 1);
    });
}
