using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.UiTests;

/// <summary>Version 0.6.0: the reports period (all / year / month), tabs, the income window, the new icon, no outside quick add.</summary>
public class V6UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    // ----- reports: all / year / month -----

    [Fact]
    public void Reports_StartWithTheYear_AndRememberTheChoiceForTheNextRun() => WithApp(app =>
    {
        var store = app.Get<SettingsStore>();
        var vm = app.Get<ReportsViewModel>();
        Assert.Equal(ReportPeriod.Year, vm.Scope);                                        // the default is always the year
        Assert.True(vm.IsYear);
        Assert.Equal(DateTime.Today.Year.ToString(), vm.Title);

        vm.SetScopeCommand.Execute("Month");
        Assert.True(vm.IsMonth);
        Assert.Equal(ReportPeriod.Month, store.Current.ReportsPeriod);                    // saved at once

        vm.SetScopeCommand.Execute("All");
        Assert.Equal(ReportPeriod.All, store.Current.ReportsPeriod);

        // the next run of the program: a new view model reads the saved choice
        var next = new ReportsViewModel(app.Core, app.Get<DataHub>(), app.Get<ExportActions>(), store);
        Assert.Equal(ReportPeriod.All, next.Scope);
        Assert.True(next.IsAll);
    });

    [Fact]
    public void Reports_EverythingCoversAllTheDataAndHasNothingToCompareWith() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        vm.SetScopeCommand.Execute("All");

        Assert.Equal("Cały okres", vm.Title);
        Assert.False(vm.HasArrows);
        Assert.False(vm.ShowComparison);
        Assert.Empty(vm.Comparison);
        Assert.Equal("", vm.ComparisonNote);

        var all = app.Core.Reader.GetTransactions();
        var expenses = all.Where(t => t.Type == TransactionType.Expense).Sum(t => t.AmountPln);
        Assert.Contains(expenses.ToString("C2", System.Globalization.CultureInfo.CurrentCulture), vm.ExpenseText);

        var anchor = vm.Anchor;
        vm.PreviousPeriod();                                                               // no previous "everything"
        vm.NextPeriod();
        Assert.Equal(anchor, vm.Anchor);

        Assert.True(vm.HasBreakdown);                                                      // the categories cover everything too
        Assert.NotEmpty(vm.TrendSeries);
        Assert.Contains("całym okresie", vm.TrendTitle);

        vm.SetScopeCommand.Execute("Year");
        Assert.True(vm.HasArrows);
        Assert.True(vm.ShowComparison);
        Assert.Equal(3, vm.Comparison.Count);
    });

    [Fact]
    public void Reports_TheTopShowsEverythingYearMonthInThatOrder_AndAnExcelIcon() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1300, 900);

        var radios = UiHost.Descendants<RadioButton>(view).Where(r => r.GroupName == "scope").OrderBy(r => r.TranslatePoint(new Point(0, 0), view).X).Select(r => r.Content?.ToString()).ToList();
        Assert.Equal(["Wszystko", "Rok", "Miesiąc"], radios);

        var buttons = UiHost.Descendants<Button>(view).Where(UiHost.IsShown).ToList();
        Assert.DoesNotContain(buttons, b => b.Content is string { Length: > 0 } text && text.Contains("Excel"));   // the text button is gone
        var excel = buttons.Single(b => b.Command == vm.ExportCommand);
        Assert.IsType<Border>(excel.Content);                                                                    // an icon, as in Transactions
    });

    [Fact]
    public void Reports_TheTabsStayInPlaceWhileTheContentScrolls() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1200, 600);

        var tabs = UiHost.Descendants<TabControl>(view).Single();
        var scroll = UiHost.Descendants<ScrollViewer>(view).First(s => s.Content is StackPanel);
        Assert.False(UiHost.Descendants<TabControl>(scroll).Any());                       // the tabs are not inside what scrolls
        Assert.Equal(System.Windows.Media.Brushes.Transparent, tabs.Background);          // the same look as the tabs of Settings
        Assert.Equal(3, tabs.Items.Count);
        Assert.Equal(new Settings_TabLook().Border, tabs.BorderThickness.Left);
    });

    private sealed class Settings_TabLook
    {
        public double Border => 0;
    }

    [Fact]
    public void Reports_ExportOfEverythingIsTheListOfAllTransactions() => WithApp(app =>
    {
        var dialogs = (FakeDialogs)app.Get<IFileDialogs>();
        var path = Path.Combine(Path.GetTempPath(), "budzet-export-" + Guid.NewGuid().ToString("N") + ".xlsx");
        dialogs.NextSave = path;
        try
        {
            var vm = app.Get<ReportsViewModel>();
            vm.SetScopeCommand.Execute("All");
            vm.ExportCommand.Execute(null);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 1000);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    });

    // ----- the income window -----

    [Fact]
    public void Income_HasItsOwnSimpleWindow_WithoutPlaceEventOrItems() => WithApp(app =>
    {
        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: true);
        var window = new IncomeWindow(vm);
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 560, 420);
        var texts = UiHost.Texts(content);
        var buttons = UiHost.Descendants<Button>(content).Select(b => b.Content?.ToString()).ToList();
        window.Content = null;
        content.DataContext = vm;
        UiHost.Render(content, 560, 420, "income");

        Assert.Contains("Nowy przychód", texts);
        foreach (var field in new[] { "Data", "Kwota", "Waluta", "Kategoria", "Notatka" }) Assert.Contains(field, texts);
        foreach (var gone in new[] { "Miejsce", "Wydarzenie", "Kwota łączna" }) Assert.DoesNotContain(gone, texts);
        Assert.DoesNotContain("+", buttons);                                               // no items to add
        Assert.Contains("Zatwierdź", buttons);
        Assert.Empty(UiHost.Descendants<ItemsControl>(content).Where(i => i.Name == "LinesList"));

        // the expense window still has all of them
        var expense = new EntryWindow(new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: false));
        var expenseContent = (FrameworkElement)expense.Content;
        UiHost.Layout(expenseContent, 780, 680);
        var expenseTexts = UiHost.Texts(expenseContent);
        Assert.Contains("Miejsce", expenseTexts);
        Assert.Contains("Wydarzenie", expenseTexts);
        Assert.Contains("Kwota łączna", expenseTexts);
    });

    [Fact]
    public void Income_SavesOneTransactionWithCategoryNoteAndCurrency_AndNeverTakesAnEvent() => WithApp(app =>
    {
        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: true);
        Assert.Equal(Guid.Empty, vm.EventKey);                                              // the running holiday is not suggested for an income
        vm.Date = DateTime.Today.AddDays(-1);
        Assert.Equal(Guid.Empty, vm.EventKey);

        var category = vm.Categories.First(c => c.Category.Name == "Wynagrodzenie").Id;
        vm.TotalText = "1000+250,50";
        vm.Main.CategoryId = category;
        vm.Note = "premia";
        var before = app.Core.Reader.GetTransactions().Count;

        Assert.True(vm.Confirm());
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);
        var saved = app.Core.Reader.GetTransactions().Single(t => t.Note == "premia");
        Assert.Equal((TransactionType.Income, 1250.5m, category, ""), (saved.Type, saved.Amount, saved.CategoryId, saved.Place));
        Assert.Null(saved.EventId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), saved.Date);
    });

    // ----- the icon and the removed feature -----

    [Fact]
    public void Icon_HasEveryStandardSize_AndTheBackgroundAroundThePigIsTransparent()
    {
        var uri = new Uri("pack://application:,,,/BudzetDomowy.App;component/Assets/BudzetDomowy.ico", UriKind.Absolute);
        var info = Application.GetResourceStream(uri);
        Assert.NotNull(info);
        using var stream = info.Stream;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        var count = BitConverter.ToInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i]).ToList();
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], sizes);

        // the 256 px frame is a PNG with a transparent corner and an opaque middle
        var offset = BitConverter.ToInt32(bytes, 6 + 16 * (count - 1) + 12);
        var length = BitConverter.ToInt32(bytes, 6 + 16 * (count - 1) + 8);
        using var png = new MemoryStream(bytes, offset, length);
        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0],
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[256 * 256 * 4];
        frame.CopyPixels(pixels, 256 * 4, 0);
        Assert.Equal(0, pixels[3]);                                                          // top-left corner: transparent
        Assert.Equal(255, pixels[(128 * 256 + 128) * 4 + 3]);                                // the middle of the pig: solid
    }

    [Fact]
    public void QuickAddFromOutsideTheProgram_IsGone()
    {
        Assert.Null(typeof(AppSettings).GetProperty("BackgroundMode"));
        Assert.DoesNotContain(ShortcutMap.Definitions, d => d.Id.StartsWith("Global.", StringComparison.Ordinal));
        var assembly = typeof(AppSettings).Assembly;
        Assert.Null(assembly.GetType("BudzetDomowy.App.Infrastructure.TrayHotkeyHost"));
        Assert.Null(assembly.GetType("BudzetDomowy.App.Infrastructure.IBackgroundHost"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name == "System.Windows.Forms");   // no tray icon any more
    }

    [Fact]
    public void Settings_HasNoBackgroundModeOption() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        Assert.Null(typeof(SettingsViewModel).GetProperty("BackgroundMode"));
        var view = new SettingsView { DataContext = vm };
        UiHost.Layout(view, 1100, 900);
        Assert.DoesNotContain(UiHost.Texts(view), t => t.Contains("zasobniku") || t.Contains("Ctrl + Alt + B"));
        Assert.DoesNotContain(vm.Shortcuts, r => r.Keys.Contains("Alt + B"));
    });
}
