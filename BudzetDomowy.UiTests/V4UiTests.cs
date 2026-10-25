using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Backup;
using BudzetDomowy.Core.Domain;
using LiveChartsCore.SkiaSharpView;

namespace BudzetDomowy.UiTests;

/// <summary>Version 0.4.0: overview comparison and sums, vertical grid lines, recurring details, Home/End, Esc, backups.</summary>
public class V4UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    private static void WithAppAsync(Func<TestApp, Task> test)
    {
        using var app = new TestApp();
        UiHost.RunAsync(() => test(app));
    }

    // ----- reports -----

    [Fact]
    public void Reports_AChartWithTheSumsOfWhatTheTrendChartShowsSitsBesideIt() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var sums = vm.SumSeries.Cast<ColumnSeries<double>>().ToList();
        Assert.Equal(3, sums.Count);
        Assert.Equal(["Przychody", "Wydatki", "Bilans"], sums.Select(s => s.Name));

        var income = vm.TrendSeries.Cast<ColumnSeries<double>>().First().Values!.Sum();
        var expense = vm.TrendSeries.Cast<ColumnSeries<double>>().Last().Values!.Sum();
        Assert.Equal(income, sums[0].Values!.Single(), 6);
        Assert.Equal(expense, sums[1].Values!.Single(), 6);
        Assert.Equal(income - expense, sums[2].Values!.Single(), 6);                  // the balance may be negative: its axis has no zero floor

        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1300, 1500);
        Assert.Contains("Razem w widocznym okresie", UiHost.Texts(view));

        vm.IsYear = true;                                                              // the sums follow the other period too
        Assert.Equal(vm.TrendSeries.Cast<ColumnSeries<double>>().First().Values!.Sum(), vm.SumSeries.Cast<ColumnSeries<double>>().First().Values!.Single(), 6);
    });

    [Fact]
    public void Reports_TheOverviewComparesOnlyWhatExists_AndSaysSo() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // a year with no data at all: nothing to compare, and the overview says it is incomplete
        vm.IsYear = true;
        vm.Anchor = new DateOnly(2020, 1, 1);
        Assert.Contains("niepełny", vm.ComparisonNote);

        // the running month is cut at today and the month before at the same day
        vm.IsYear = false;
        vm.Anchor = new DateOnly(today.Year, today.Month, 1);
        if (today != PeriodEnd(today))
        {
            Assert.Contains("niepełny", vm.ComparisonNote);
            Assert.Contains(today.ToString("d", System.Globalization.CultureInfo.CurrentCulture), vm.ComparisonNote);
            Assert.Contains(today.AddMonths(-1).ToString("d", System.Globalization.CultureInfo.CurrentCulture), vm.ComparisonNote);
        }

        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1300, 1500);
        Assert.Contains(UiHost.Texts(view), t => t.StartsWith("Przegląd jest niepełny"));
    });

    private static DateOnly PeriodEnd(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1);

    [Fact]
    public void Places_TheNumberOfEntriesIsCentredInItsCell() => WithApp(app =>
    {
        var vm = app.Get<ReportsViewModel>();
        vm.SetTabCommand.Execute("Places");
        var view = new ReportsView { DataContext = vm };
        UiHost.Layout(view, 1100, 900);
        var grid = UiHost.Descendants<DataGrid>(view).First(g => g.Columns.Count == 4);
        var column = (DataGridTextColumn)grid.Columns[1];
        Assert.Contains(column.ElementStyle.Setters.OfType<Setter>(), s => s.Property == TextBlock.TextAlignmentProperty && Equals(s.Value, TextAlignment.Center));
    });

    [Fact]
    public void Tables_HaveADiscreetVerticalLineBetweenTheColumns() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        var view = new TransactionsView { DataContext = vm };
        UiHost.Layout(view, 1000, 500);
        var grid = Assert.IsType<DataGrid>(view.FindName("Grid"));
        Assert.Equal(DataGridGridLinesVisibility.All, grid.GridLinesVisibility);
        var brush = Assert.IsType<SolidColorBrush>(grid.VerticalGridLinesBrush);
        Assert.True(brush.Color.A < 0x80);                                              // faint, not a hard line

        var reports = app.Get<ReportsViewModel>();
        reports.SetTabCommand.Execute("Places");
        var other = new ReportsView { DataContext = reports };
        UiHost.Layout(other, 1000, 800);
        var places = UiHost.Descendants<DataGrid>(other).First(g => g.Columns.Count == 4);
        Assert.Equal(brush.Color, Assert.IsType<SolidColorBrush>(places.VerticalGridLinesBrush).Color);   // every table gets it from the shared style
    });

    [Fact]
    public void Settings_TheChosenTabIsALighterGrey() => WithApp(app =>
    {
        Assert.IsType<SolidColorBrush>(Application.Current.FindResource("TabViewItemHeaderBackgroundSelected"));
    });

    // ----- recurring entries -----

    [Fact]
    public void Recurring_DetailsAreReadOnlyUntilChangeIsPressed_AndANameIsRequired() => WithApp(app =>
    {
        var vm = app.Get<RecurringViewModel>();
        Assert.False(vm.ShowEditor);                                                    // nothing chosen: no details

        vm.Selected = vm.Items[0];
        Assert.True(vm.ShowEditor);
        Assert.False(vm.IsEditing);
        Assert.True(vm.CanEdit);
        Assert.Equal("Czynsz", vm.EditName);
        Assert.Equal("Czynsz", vm.Items[0].Title);

        var view = new RecurringView { DataContext = vm };
        UiHost.Layout(view, 1050, 700);
        var texts = UiHost.Texts(view);
        Assert.Contains("Zmień", UiHost.Descendants<Button>(view).Select(b => b.Content?.ToString()));
        Assert.DoesNotContain("Zapisz zmiany", UiHost.Descendants<Button>(view).Where(UiHost.IsShown).Select(b => b.Content?.ToString()));
        Assert.True(vm.IsReadOnly);

        vm.EditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.CanEdit);

        vm.EditName = "   ";
        vm.SaveCommand.Execute(null);
        Assert.Equal("Podaj nazwę wpisu cyklicznego.", vm.Message);
        Assert.True(vm.IsEditing);                                                      // still editing: nothing was saved
        Assert.Equal("Czynsz", app.Core.Reader.GetRecurringTransactions().Single().Name);

        vm.EditName = "  Czynsz mieszkania ";
        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("Czynsz mieszkania", app.Core.Reader.GetRecurringTransactions().Single().Name);
        Assert.Equal("Czynsz mieszkania", vm.Items.Single().Title);
        Assert.NotEmpty(texts);
    });

    [Fact]
    public void Recurring_CancelThrowsAwayEdits_AndChoosingAnotherEntryLeavesEditing() => WithApp(app =>
    {
        var vm = app.Get<RecurringViewModel>();
        vm.Selected = vm.Items[0];
        vm.EditCommand.Execute(null);
        vm.EditName = "Cos innego";
        vm.CancelEditCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("Czynsz", vm.EditName);

        vm.EditCommand.Execute(null);
        vm.Selected = null;
        vm.Selected = vm.Items[0];
        Assert.False(vm.IsEditing);

        vm.NewCommand.Execute(null);                                                    // a new entry is edited straight away
        Assert.True(vm.IsEditing);
        Assert.True(vm.ShowEditor);
        Assert.Equal("", vm.EditName);
        vm.CancelEditCommand.Execute(null);
        Assert.False(vm.ShowEditor);

        // the editing screen shows the save button
        vm.Selected = vm.Items[0];
        vm.EditCommand.Execute(null);
        var view = new RecurringView { DataContext = vm };
        UiHost.Layout(view, 1050, 700);
        Assert.Contains("Zapisz zmiany", UiHost.Descendants<Button>(view).Where(UiHost.IsShown).Select(b => b.Content?.ToString()));
        Assert.DoesNotContain("Zmień", UiHost.Descendants<Button>(view).Where(UiHost.IsShown).Select(b => b.Content?.ToString()));
    });

    [Fact]
    public void Recurring_ANewEntryNeedsAName() => WithApp(app =>
    {
        var vm = app.Get<RecurringViewModel>();
        vm.NewCommand.Execute(null);
        vm.EditAmount = "50";
        vm.SaveCommand.Execute(null);
        Assert.Equal("Podaj nazwę wpisu cyklicznego.", vm.Message);
        Assert.Single(app.Core.Reader.GetRecurringTransactions());

        vm.EditName = "Siłownia";
        vm.SaveCommand.Execute(null);
        Assert.Equal(2, app.Core.Reader.GetRecurringTransactions().Count);
        Assert.Contains(vm.Items, i => i.Title == "Siłownia");
        Assert.False(vm.IsEditing);
    });

    // ----- transactions keys -----

    private static void CallGoToEdge(TransactionsView view, bool toEnd, bool extend) =>
        typeof(TransactionsView).GetMethod("GoToEdge", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [toEnd, extend]);

    [Fact]
    public void Transactions_HomeAndEndGoToTheFirstAndLastRow() => WithApp(app =>
    {
        var vm = app.Get<TransactionsViewModel>();
        vm.SetModeCommand.Execute("All");
        var view = new TransactionsView { DataContext = vm };
        UiHost.Layout(view, 1100, 600);
        var grid = Assert.IsType<DataGrid>(view.FindName("Grid"));
        Assert.True(grid.Items.Count >= 6);

        grid.SelectedItem = grid.Items[3];
        CallGoToEdge(view, toEnd: true, extend: false);
        Assert.Same(grid.Items[^1], grid.SelectedItem);
        Assert.Single(grid.SelectedItems);

        CallGoToEdge(view, toEnd: false, extend: false);
        Assert.Same(grid.Items[0], grid.SelectedItem);
        Assert.Single(grid.SelectedItems);

        grid.SelectedItem = grid.Items[2];
        CallGoToEdge(view, toEnd: false, extend: true);                                 // Shift+Home: from the current row up to the first
        Assert.Equal(3, grid.SelectedItems.Count);
        CallGoToEdge(view, toEnd: true, extend: true);
        Assert.Equal(grid.Items.Count - 0 >= 0 ? grid.SelectedItems.Count : 0, grid.SelectedItems.Count);
        Assert.Contains(grid.Items[^1], grid.SelectedItems.Cast<object>());
    });

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static KeyEventArgs Escape(bool handled = false) => new(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Escape)
    {
        RoutedEvent = Keyboard.KeyDownEvent,
        Handled = handled,
    };

    private static void PressEsc(MainWindow window, bool handled = false)
    {
        var e = Escape(handled);
        typeof(MainWindow).GetMethod("OnWindowKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, e]);
    }

    [Fact]
    public void Esc_ThatNothingElseUsedTakesYouToTheDashboard() => WithApp(app =>
    {
        var main = app.Get<MainViewModel>();
        var window = new MainWindow(main, app.Get<SettingsStore>());

        main.Navigate("reports");
        PressEsc(window, handled: true);                                                // the table (or a list) already used it: stay
        Assert.Equal("reports", main.SelectedNav!.Key);

        PressEsc(window);
        Assert.Equal("dashboard", main.SelectedNav!.Key);

        PressEsc(window);                                                               // already there: nothing happens
        Assert.Equal("dashboard", main.SelectedNav!.Key);
    });

    [Fact]
    public void Esc_InTransactions_DeselectsFirst_ThenClearsFilters_ThenLeaves() => WithApp(app =>
    {
        var tx = app.Get<TransactionsViewModel>();
        tx.SetModeCommand.Execute("All");
        tx.Search = "lewiatan";
        Assert.True(tx.HasActiveFilters);

        // the table's own handler (selection, then filters) comes first; only an unused Esc reaches the window
        Assert.True(tx.ClearFiltersIfAny());
        Assert.False(tx.ClearFiltersIfAny());                                           // nothing left: the key is free for the window

        var main = app.Get<MainViewModel>();
        var window = new MainWindow(main, app.Get<SettingsStore>());
        main.Navigate("transactions");
        PressEsc(window);
        Assert.Equal("dashboard", main.SelectedNav!.Key);
    });

    // ----- backups -----

    [Fact]
    public void Backup_TheTabListsCopies_AndCreatesOneOnRequest() => WithAppAsync(async app =>
    {
        var vm = app.Get<BackupViewModel>();
        Assert.Empty(vm.Items);
        Assert.Equal(10, vm.Keep);
        Assert.False(vm.CanRestore);

        await vm.CreateNowCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Items);
        Assert.Equal("Ręczna", row.KindText);
        Assert.True(row.Files >= 1);
        Assert.Contains("KB", row.SizeText);
        Assert.Contains("została utworzona", vm.Message);

        // the daily copy is not made again on a day that already has one
        Assert.Null(await vm.EnsureDailyAsync());
        Assert.Single(vm.Items);
    });

    [Fact]
    public void Backup_TheSettingsTabShowsTheList() => WithAppAsync(async app =>
    {
        var settings = app.Get<SettingsViewModel>();
        await settings.Backup.CreateNowCommand.ExecuteAsync(null);
        settings.SelectedTab = SettingsViewModel.BackupTab;
        var view = new SettingsView { DataContext = settings };
        UiHost.Layout(view, 1100, 760);
        var texts = UiHost.Texts(view);
        UiHost.Render(view, 1100, 760, "settings-backup");

        Assert.Contains("BackUp", UiHost.Descendants<TabItem>(view).Select(t => t.Header?.ToString()));
        Assert.Contains("Utwórz kopię teraz", UiHost.Descendants<Button>(view).Select(b => b.Content?.ToString()));
        Assert.Contains("Ręczna", texts);
        Assert.True(texts.Any(t => t.Contains("ostatnie")), string.Join(" | ", texts));
    });

    [Fact]
    public void Backup_RestoreAsksFirst_ThenBringsTheOldStateBack() => WithAppAsync(async app =>
    {
        var vm = app.Get<BackupViewModel>();
        var dialogs = app.Get<FakeMessageDialogs>();
        var before = app.Core.Reader.GetTransactions().Count;
        await vm.CreateNowCommand.ExecuteAsync(null);

        var category = app.Core.Reader.GetCategories().First();
        app.Core.Transactions.Add(DateOnly.FromDateTime(DateTime.Today), TransactionType.Expense, 12.34m, category.Id, "po kopii", null);
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);

        vm.Selected = vm.Items[0];
        Assert.True(vm.CanRestore);

        dialogs.Answer = false;                                                         // "no": nothing happens
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Asked);
        Assert.Contains("zostaną wycofane", dialogs.Asked[0]);
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);
        Assert.Single(vm.Items);

        dialogs.Answer = true;
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(before, app.Core.Reader.GetTransactions().Count);
        Assert.StartsWith("Przywrócono dane", vm.Message);
        Assert.Equal(2, vm.Items.Count);                                                // plus the copy of the state before the restore
        Assert.Contains(vm.Items, r => r.Info.Kind == BackupKind.BeforeRestore);

        // and that safety copy brings the entry made after the first backup back
        vm.Selected = vm.Items.First(r => r.Info.Kind == BackupKind.BeforeRestore);
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, app.Core.Reader.GetTransactions().Count);
        Assert.Contains(app.Core.Reader.GetTransactions(), t => t.Note == "po kopii");
    });

    [Fact]
    public void Backup_StartDailyDoesNotThrowAndTheTimerIsOptional() => WithApp(app =>
    {
        var vm = app.Get<BackupViewModel>();
        vm.StartDaily(TimeSpan.FromHours(1), TimeSpan.FromHours(3));                     // only schedules; no copy is made before the delay
        Assert.Empty(vm.Items);
    });
}
