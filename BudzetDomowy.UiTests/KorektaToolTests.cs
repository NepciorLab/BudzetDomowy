using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using ClosedXML.Excel;
using K = BudzetDomowy.Korekta;

namespace BudzetDomowy.UiTests;

/// <summary>The separate tool for one-off corrections of the data: its window logic, run against a real data folder in a temporary place.</summary>
public sealed class KorektaToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-korekta-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly List<BudgetCore> _cores = [];

    public KorektaToolTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class FakeUi : K.IUi
    {
        public bool Answer { get; set; } = true;
        public string? SavePath { get; set; }
        public List<string> Asked { get; } = [];
        public string? PickFolder(string? initial) => null;
        public string? PickSaveFile(string defaultName) => SavePath;
        public bool Confirm(string title, string message)
        {
            Asked.Add(title + "|" + message);
            return Answer;
        }
        public void ShowMessage(string title, string message) { }
    }

    /// <summary>Another computer (or the main program) working on the same folder.</summary>
    private BudgetCore MainProgram()
    {
        var core = BudgetCore.Create(Path.Combine(_root, "main-local"), _data, "GŁÓWNY", rateSource: new NoRates());
        core.Categories.SeedIfEmpty();
        _cores.Add(core);
        return core;
    }

    private BudgetCore Seed()
    {
        var core = MainProgram();
        var day = new DateOnly(2026, 2, 1);
        for (var i = 0; i < 10; i++)
            core.Transactions.Add(day.AddDays(i * 2), TransactionType.Expense, 100m + i, null, "n" + i, null, place: i % 2 == 0 ? "Biedronka" : "Orlen");
        core.Transactions.Add(new DateOnly(2026, 2, 5), TransactionType.Income, 5000m, null, "pensja", null, place: "Firma");
        core.Transactions.Add(new DateOnly(2026, 2, 6), TransactionType.Expense, 10m, null, "euro", null, place: "Sklep", currency: "EUR", rateToPln: 4.3m);
        return core;
    }

    private async Task<K.MainViewModel> OpenAsync(FakeUi ui)
    {
        var vm = new K.MainViewModel(ui, Path.Combine(_root, "tool"));
        await vm.LoadAsync(_data);
        Assert.True(vm.IsLoaded, vm.Status);
        return vm;
    }

    /// <summary>The thousands separator of the Polish culture is a no-break space: the tests compare with an ordinary one.</summary>
    private static string Plain(string text) => text.Replace('\u00A0', ' ').Replace('\u202F', ' ');

    private static void Select(K.MainViewModel vm, Func<K.TxRow, bool> where) => vm.SetSelection(vm.Rows.Where(where).ToList());

    // ----- loading and filtering -----

    [Fact]
    public void TheTool_LoadsTheWholeDataFolder_AndFiltersLikeTheMainProgram()
    {
        var seeded = Seed();
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            Assert.Equal(12, vm.Rows.Count);
            Assert.Contains("12 z 12", vm.ShownText);
            Assert.Equal("Wczytano wpisów: 12.", vm.Status);

            vm.TypeFilter = vm.TypeFilters.Single(t => t.Type == TransactionType.Income);
            Assert.Equal(["Firma"], vm.Rows.Select(r => r.Place));
            vm.TypeFilter = vm.TypeFilters[0];

            vm.PlaceText = "bied";
            Assert.Equal(5, vm.Rows.Count);
            vm.PlaceText = "";

            vm.AmountFromText = "103";
            vm.AmountToText = "105,00";
            Assert.Equal([103m, 104m, 105m], vm.Rows.Select(r => r.AmountPln).OrderBy(a => a));
            vm.AmountFromText = "";
            vm.AmountToText = "";

            vm.From = new DateTime(2026, 2, 5);
            vm.To = new DateTime(2026, 2, 7);
            Assert.Equal(4, vm.Rows.Count);                                                 // the 5th (an expense and the income), the 6th and the 7th
            Assert.Equal(43m, vm.Rows.Single(r => r.Place == "Sklep").AmountPln);          // 10 EUR at 4.30 shown in zloty
            Assert.Equal("10,00 EUR", vm.Rows.Single(r => r.Place == "Sklep").OwnAmountText);

            vm.ClearFiltersCommand.Execute(null);
            Assert.Equal(12, vm.Rows.Count);

            var food = seeded.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;
            seeded.Transactions.Update(seeded.Reader.GetTransactions().First(t => t.Place == "Orlen") with { CategoryId = food });
            await vm.ReloadCommand.ExecuteAsync(null);
            vm.CategoryFilter = vm.FilterCategories.First(c => c.Id == food);
            Assert.Single(vm.Rows);
        });
    }

    [Fact]
    public void AnUnknownFolder_IsReportedWithoutCrashing()
    {
        UiHost.RunAsync(async () =>
        {
            var vm = new K.MainViewModel(new FakeUi(), Path.Combine(_root, "tool"));
            await vm.LoadAsync(Path.Combine(_root, "nie ma takiego"));
            Assert.False(vm.IsLoaded);
            Assert.StartsWith("Nie udało się wczytać danych", vm.Status);
        });
    }

    // ----- changing amounts, then the check, then the undo -----

    [Fact]
    public void ChangingAmounts_ShowsAPlanFirst_ThenWritesChecksAndCanBeUndone()
    {
        var core = Seed();
        var ui = new FakeUi();
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(ui);
            Select(vm, r => r.Place == "Biedronka");
            Assert.Equal(5, vm.Selected.Count);
            Assert.StartsWith("Zaznaczono wpisów: 5", vm.SelectionText);

            vm.ShiftAmountText = "abc";
            vm.PlanShiftCommand.Execute(null);
            Assert.Null(vm.Plan);                                                          // nothing is planned from rubbish

            vm.ShiftAmountText = "10,50";
            vm.PlanShiftCommand.Execute(null);
            Assert.Equal(5, vm.PlanRows.Count);
            Assert.All(vm.PlanRows, r => Assert.Equal("Zmiana", r.KindText));
            Assert.Contains("Zmniejszenie kwot o 10,50 zł", vm.PlanSummary);
            Assert.Contains("wydatki -52,50 zł", vm.PlanSummary);                          // what the plan will do to the sums
            Assert.True(vm.CanApply);
            Assert.Equal(100m, core.Reader.GetTransactions().First(t => t.Note == "n0").Amount);   // nothing was written yet

            ui.Answer = false;                                                             // "no" to the question: still nothing written
            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(100m, core.Reader.GetTransactions().First(t => t.Note == "n0").Amount);
            Assert.Single(ui.Asked);
            Assert.Contains("kopię zapasową", ui.Asked[0]);

            ui.Answer = true;
            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.True(vm.Report!.IsOk);
            Assert.StartsWith("✔ Wszystko się zgadza", vm.Report.Banner);
            Assert.Equal(1, vm.SelectedTab);                                               // the window moved to the check
            Assert.Equal(5, vm.Report.Days.Count);
            Assert.Equal("Zmiana rzeczywista", vm.Report.Totals[2].Label);
            Assert.Equal("-52,50", vm.Report.Totals[2].Expense);
            Assert.Equal(["Przed", "Po", "Zmiana rzeczywista", "Zmiana zapowiedziana"], vm.Report.Totals.Select(t => t.Label));
            core.Sync.SyncOnce();
            Assert.Equal(89.5m, core.Reader.GetTransactions().First(t => t.Note == "n0").Amount);
            Assert.Null(vm.Plan);
            var item = Assert.Single(vm.History);
            Assert.Equal("Kontrola OK", item.StateText);

            await vm.RevertCommand.ExecuteAsync(null);                                      // undo the one shown in the check

            Assert.True(vm.Report!.IsOk);
            Assert.Equal("Kontrola po cofnięciu", vm.Report.Caption);
            core.Sync.SyncOnce();
            Assert.Equal(100m, core.Reader.GetTransactions().First(t => t.Note == "n0").Amount);
            Assert.Equal("Cofnięta", Assert.Single(vm.History).StateText);
            Assert.False(vm.Report.CanRevert);
        });
    }

    [Fact]
    public void IncreasingWorksToo_AndWhatWouldNotStayPositiveIsReported()
    {
        Seed();
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            Select(vm, r => !r.IsIncome && r.Model.CurrencyCode == "PLN" && r.AmountPln < 103m);
            vm.ShiftIncrease = true;
            Assert.False(vm.ShiftDecrease);
            vm.ShiftAmountText = "1000";
            vm.PlanShiftCommand.Execute(null);
            Assert.Contains("Zwiększenie kwot o 1 000,00 zł", Plain(vm.PlanSummary));
            Assert.Equal(3, vm.PlanRows.Count);

            vm.ShiftIncrease = false;                                                      // now a decrease by 101: 100 and 101 would not stay above zero
            vm.ShiftAmountText = "101";
            vm.PlanShiftCommand.Execute(null);
            Assert.Equal("Pominięte – kwota po zmianie nie byłaby większa od zera: 2.", vm.PlanSkipped);
            Assert.Equal(1, vm.PlanRows.Count);
        });
    }

    // ----- splitting -----

    [Fact]
    public void Splitting_TakesAnAmountForEachEntry_AndKeepsTheTotalsOfEveryDay()
    {
        var core = Seed();
        var sweets = core.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            Select(vm, r => r.Place == "Orlen");
            Assert.Equal(5, vm.SplitItems.Count);

            vm.PlanSplitCommand.Execute(null);
            Assert.Contains("Wybierz kategorię", vm.Status);                               // the category of the parts is needed first
            vm.SplitCategory = vm.ChoiceCategories.First(c => c.Id == sweets);
            vm.PlanSplitCommand.Execute(null);
            Assert.Contains("Wpisz kwoty", vm.Status);                                     // and an amount for at least one entry

            for (var i = 0; i < vm.SplitItems.Count; i++) vm.SplitItems[i].PartText = (10 + i).ToString(CultureInfo.CurrentCulture);
            vm.SplitItems[1].PartText = "500";                                              // larger than the entry itself
            Assert.True(vm.SplitItems[1].IsInvalid);
            vm.PlanSplitCommand.Execute(null);
            Assert.Contains("niepoprawne", vm.Status);
            Assert.Null(vm.Plan);

            vm.SplitItems[1].PartText = "11,5";
            Assert.Equal(vm.SplitItems[1].Row.Model.Amount - 11.5m, decimal.Parse(vm.SplitItems[1].RestText, CultureInfo.CurrentCulture));
            vm.PlanSplitCommand.Execute(null);
            Assert.Equal(10, vm.PlanRows.Count);                                            // five changed and five new
            Assert.Equal(5, vm.PlanRows.Count(r => r.IsAdd));
            Assert.Contains("przychody 0,00 zł, wydatki 0,00 zł, wpisów +5", vm.PlanSummary);

            var daysBefore = BudzetDomowy.Core.Corrections.DaySnapshot.Build(core.Reader.GetTransactions());
            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.True(vm.Report!.IsOk);
            core.Sync.SyncOnce();
            var after = BudzetDomowy.Core.Corrections.DaySnapshot.Build(core.Reader.GetTransactions());
            Assert.All(daysBefore, kv => Assert.Equal(kv.Value.Expense, after[kv.Key].Expense));           // the money per day is the same
            Assert.Equal(5, core.Reader.GetTransactions().Count(t => t.CategoryId == sweets));
        });
    }

    [Fact]
    public void Splitting_FillAllAndHalf_AndACategoryOfTheOtherKindLeavesTheEntryOut()
    {
        var core = Seed();
        var incomeCategory = core.Reader.GetCategories().First(c => c.Type == CategoryType.Income && c.ParentId is null);
        var sweets = core.Reader.GetCategories().First(c => c.Name == "Cukiernicze").Id;
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            Select(vm, r => r.Place == "Orlen");
            vm.SplitAllText = "7";
            vm.SplitFillAllCommand.Execute(null);
            Assert.All(vm.SplitItems, i => Assert.Equal("7", i.PartText));
            vm.SplitHalfCommand.Execute(null);
            Assert.Equal(vm.SplitItems.Select(i => (i.Row.Model.Amount / 2m).ToString("0.##", CultureInfo.CurrentCulture)), vm.SplitItems.Select(i => i.PartText));

            // an income category for expenses: nothing is planned from such entries
            vm.SplitCategory = vm.ChoiceCategories.First(c => c.Id == incomeCategory.Id);
            vm.PlanSplitCommand.Execute(null);
            Assert.Empty(vm.PlanRows);
            Assert.Contains("kategoria nie pasuje do rodzaju wpisu: 5", vm.PlanSkipped);

            vm.SplitCategory = vm.ChoiceCategories.First(c => c.Id == sweets);
            vm.PlanSplitCommand.Execute(null);
            Assert.Equal(10, vm.PlanRows.Count);
        });
    }

    // ----- adding every month -----

    [Fact]
    public void AddingEveryMonth_IncludingIncomes_ShowsTheDatesFirst()
    {
        var core = Seed();
        var incomeCategory = core.Reader.GetCategories().First(c => c.Type == CategoryType.Income && c.ParentId is null);
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            vm.AddFrom = new DateTime(2025, 1, 1);
            vm.AddTo = new DateTime(2025, 12, 31);
            vm.AddDay = 31;
            Assert.Equal("Powstanie wpisów: 12 (od 2025-01-31 do 2025-12-31).", vm.AddDatesText);

            vm.AddIncome = true;
            Assert.False(vm.AddExpense);
            vm.AddAmountText = "250";
            vm.AddPlace = "Dodatek";
            vm.AddNote = "wyrównanie";
            vm.AddCategory = vm.ChoiceCategories.First(c => c.Id == incomeCategory.Id);
            vm.PlanAddCommand.Execute(null);

            Assert.Equal(12, vm.PlanRows.Count);
            Assert.All(vm.PlanRows, r => Assert.Equal(("Nowy", "250,00"), (r.KindText, r.AfterText)));
            Assert.Equal("2025-02-28", vm.PlanRows[1].DateText);                          // the last day of a short month
            Assert.Contains("przychody +3 000,00 zł, wydatki 0,00 zł", Plain(vm.PlanSummary));

            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.True(vm.Report!.IsOk);
            Assert.Equal(12, vm.Report.Days.Count);
            var added = core.Reader.GetTransactions().Where(t => t.Place == "Dodatek").ToList();
            core.Sync.SyncOnce();
            added = core.Reader.GetTransactions().Where(t => t.Place == "Dodatek").ToList();
            Assert.Equal((12, TransactionType.Income, 250m, incomeCategory.Id), (added.Count, added[0].Type, added[0].AmountPln, added[0].CategoryId));
        });
    }

    [Fact]
    public void AddingEveryMonth_RefusesWhatCannotBeDone()
    {
        var core = Seed();
        var food = core.Reader.GetCategories().First(c => c.Name == "Spożywcze" && c.ParentId is null).Id;
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            vm.PlanAddCommand.Execute(null);
            Assert.Contains("kwotę większą od zera", vm.Status);

            vm.AddAmountText = "10";
            vm.AddFrom = new DateTime(2025, 6, 1);
            vm.AddTo = new DateTime(2025, 1, 1);
            Assert.StartsWith("Brak dat", vm.AddDatesText);
            vm.PlanAddCommand.Execute(null);
            Assert.Null(vm.Plan);

            vm.AddFrom = new DateTime(2025, 1, 1);
            vm.AddTo = new DateTime(2025, 3, 31);
            vm.AddIncome = true;
            vm.AddCategory = vm.ChoiceCategories.First(c => c.Id == food);                  // an expense category for an income
            vm.PlanAddCommand.Execute(null);
            Assert.Contains("nie pasuje do rodzaju", vm.Status);
            Assert.Null(vm.Plan);
        });
    }

    // ----- the data changed meanwhile -----

    [Fact]
    public void WhenTheDataChangesAfterThePlan_NothingIsWritten()
    {
        var core = Seed();
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            Select(vm, r => r.Place == "Orlen");
            vm.ShiftAmountText = "1";
            vm.PlanShiftCommand.Execute(null);
            Assert.True(vm.CanApply);

            var touched = core.Reader.GetTransactions().First(t => t.Place == "Orlen");
            core.Transactions.Update(touched with { Note = "zmienione w programie głównym" });

            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.StartsWith("Nic nie zapisano", vm.Status);
            Assert.Empty(vm.History);
            Assert.Equal(touched.Amount, core.Reader.GetTransactions().First(t => t.Id == touched.Id).Amount);
            Assert.Null(vm.Plan);
        });
    }

    // ----- the report, the history -----

    [Fact]
    public void TheReportGoesToExcel_AndTheHistoryShowsTheSavedChecks()
    {
        Seed();
        var ui = new FakeUi { SavePath = Path.Combine(_root, "raport.xlsx") };
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(ui);
            Select(vm, r => r.Place == "Orlen");
            vm.ShiftAmountText = "2";
            vm.PlanShiftCommand.Execute(null);
            await vm.ApplyCommand.ExecuteAsync(null);

            await vm.ExportCommand.ExecuteAsync(null);
            using (var wb = new XLWorkbook(ui.SavePath))
            {
                Assert.Equal(["Podsumowanie", "Dni"], wb.Worksheets.Select(w => w.Name));
                Assert.Contains("wszystko się zgadza", wb.Worksheet("Podsumowanie").Cell(2, 1).GetString());
            }

            vm.Report = null;
            Assert.False(vm.HasReport);
            vm.SelectedHistory = vm.History[0];
            vm.ShowSelectedHistoryCommand.Execute(null);
            Assert.True(vm.HasReport);
            Assert.True(vm.Report!.IsOk);
            Assert.StartsWith("Kontrola zapisana przy korekcie z", vm.Report.Caption);
            Assert.Equal(5, vm.Report.Days.Count);

            // a second computer sees the correction and its history through the data folder
            var other = BudgetCore.Create(Path.Combine(_root, "other"), _data, "DRUGI", rateSource: new NoRates());
            _cores.Add(other);
            other.Sync.SyncOnce();
            Assert.Single(new BudzetDomowy.Core.Corrections.CorrectionRunner(other).History());
        });
    }

    // ----- the window -----

    [Fact]
    public void TheWindow_BuildsAndShowsTheThreePlacesOfWork()
    {
        Seed();
        UiHost.RunAsync(async () =>
        {
            var vm = await OpenAsync(new FakeUi());
            var window = new K.MainWindow(vm);
            var content = (FrameworkElement)window.Content;
            UiHost.Layout(content, 1340, 860);

            var tabs = Assert.IsType<TabControl>(window.FindName("Tabs"));
            Assert.Equal(["1. Korekta", "2. Kontrola przed i po", "3. Historia korekt"], tabs.Items.Cast<TabItem>().Select(t => t.Header?.ToString()));
            var operations = Assert.IsType<TabControl>(window.FindName("Operations"));
            Assert.Equal(["Zmiana kwot", "Rozbijanie", "Dodawanie co miesiąc"], operations.Items.Cast<TabItem>().Select(t => t.Header?.ToString()));
            var grid = Assert.IsType<DataGrid>(window.FindName("EntriesGrid"));
            Assert.Equal(12, grid.Items.Count);
            Assert.Equal(["Data", "Rodzaj", "Kwota (zł)", "W walucie", "Kategoria", "Miejsce", "Notatka"], grid.Columns.Select(c => c.Header?.ToString()));

            grid.SelectedItems.Add(grid.Items[0]);                                          // the selection reaches the view model
            grid.SelectedItems.Add(grid.Items[1]);
            Assert.Equal(2, vm.Selected.Count);
            ((Button)window.FindName("SelectAllButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(12, vm.Selected.Count);

            vm.ShiftAmountText = "5";
            vm.PlanShiftCommand.Execute(null);
            UiHost.Layout(content, 1340, 860);
            Assert.Contains("Zmniejszenie kwot o 5,00 zł", UiHost.Texts(content).First(t => t.StartsWith("Zmniejszenie")));
            Assert.True(((Button)window.FindName("ApplyButton")!).IsEnabled);
            Render(window, "korekta-plan");

            await vm.ApplyCommand.ExecuteAsync(null);
            UiHost.Layout(content, 1340, 860);
            Assert.StartsWith("✔", ((TextBlock)window.FindName("ReportBanner")!).Text);
            tabs.SelectedIndex = 1;
            UiHost.Layout(content, 1340, 860);
            Render(window, "korekta-kontrola");
            tabs.SelectedIndex = 2;
            UiHost.Layout(content, 1340, 860);
            Render(window, "korekta-historia");
            Assert.Single(((DataGrid)window.FindName("HistoryGrid")!).Items);
        });
    }

    /// <summary>Draws the window content where it is (taking it out of the window would clear the selection) on the page colour into a PNG.</summary>
    private static void Render(K.MainWindow window, string name)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1340, 860, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle((System.Windows.Media.Brush)Application.Current.FindResource("SystemControlPageBackgroundAltHighBrush"), null, new Rect(0, 0, 1340, 860));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(UiHost.OutputDir, name + ".png"));
        encoder.Save(stream);
    }

    // ----- small things -----

    [Fact]
    public void TheFolderOfTheMainProgramIsFoundNextToTheTool_AndEveryFolderHasItsOwnWorkplace()
    {
        var exeFolder = Path.Combine(_root, "program");
        Directory.CreateDirectory(Path.Combine(exeFolder, "Data"));
        File.WriteAllText(Path.Combine(exeFolder, "Data", "settings.json"), "{\"DataFolder\": \"C:\\\\OneDrive\\\\Budzet\", \"Theme\": \"Dark\"}");
        Assert.Equal(@"C:\OneDrive\Budzet", K.FolderMemory.FromMainProgram(exeFolder));
        File.WriteAllText(Path.Combine(exeFolder, "Data", "settings.json"), "{\"dataFolder\": \"D:\\\\Dane\"}");
        Assert.Equal(@"D:\Dane", K.FolderMemory.FromMainProgram(exeFolder));
        File.WriteAllText(Path.Combine(exeFolder, "Data", "settings.json"), "to nie json");
        Assert.Null(K.FolderMemory.FromMainProgram(exeFolder));
        Assert.Null(K.FolderMemory.FromMainProgram(Path.Combine(_root, "nie ma")));

        var a = K.Session.LocalDirFor(@"C:\Dane\Budzet", "R");
        Assert.Equal(a, K.Session.LocalDirFor(@"c:\dane\budzet\", "R"));                    // the same folder written another way
        Assert.NotEqual(a, K.Session.LocalDirFor(@"C:\Dane\Inny", "R"));
    }
}
