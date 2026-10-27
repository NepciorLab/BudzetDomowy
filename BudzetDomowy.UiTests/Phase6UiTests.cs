using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.UiTests;

public class Phase6UiTests
{
    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    [Fact]
    public void Diagnostics_ListsDevicesAndRebuildsTheDatabaseFromTheJournals()
    {
        using var app = new TestApp();
        app.Core.Sync.SyncOnce();
        var vm = UiHost.Run(() => app.Get<DiagnosticsViewModel>());
        UiHost.Run(() => vm.Refresh());
        var device = Assert.Single(vm.Devices);
        Assert.True(device.EventCount > 10);
        Assert.Contains("TEST-PC", device.Name);
        Assert.Matches(@"\d", device.JournalSize);
        Assert.Contains("0.7.3", vm.Version);

        var before = app.Core.Reader.GetTransactions().Count;
        UiHost.RunAsync(async () =>
        {
            await vm.RebuildCommand.ExecuteAsync(null);
            Assert.Contains("odbudowana", vm.Message);
        });
        Assert.Equal(before, app.Core.Reader.GetTransactions().Count);
    }

    [Fact]
    public void Rebuild_IsRefused_WhileJournalsCannotBeRead()
    {
        var cloud = new SyncStatus { State = SyncState.Problem, Issues = [new SyncIssue(SyncIssueKind.CloudOnlyFile, "journal_x.jsonl", "cloud")] };
        var conflict = new SyncStatus { State = SyncState.Problem, Issues = [new SyncIssue(SyncIssueKind.ConflictCopy, "journal_x-PC.jsonl", "copy")] };
        Assert.True(SyncTexts.BlocksRebuild(cloud));
        Assert.False(SyncTexts.BlocksRebuild(conflict));   // a conflict copy is only a warning
        Assert.False(SyncTexts.BlocksRebuild(new SyncStatus()));
    }

    [Fact]
    public void SettingsView_RendersTheDiagnosticsAndShortcutsTabs() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        var view = new SettingsView { DataContext = vm };

        vm.SelectedTab = 4;
        UiHost.Layout(view, 1100, 760);
        var diag = UiHost.Texts(view);
        UiHost.Render(view, 1100, 760, "settings-diagnostics");
        Assert.Contains("Wersja programu", diag);

        // a view that was rendered is detached from its page; the second tab gets a fresh view
        vm.SelectedTab = SettingsViewModel.ShortcutsTab;
        var second = new SettingsView { DataContext = vm };
        UiHost.Layout(second, 1100, 760);
        var keys = UiHost.Texts(second);
        UiHost.Render(second, 1100, 760, "settings-shortcuts");
        Assert.Contains("F1", keys);
        Assert.Equal(21, vm.Shortcuts.Count);
    });

    [Fact]
    public void MainWindow_F1Command_OpensTheShortcutsTab() => WithApp(app =>
    {
        var vm = app.Get<MainViewModel>();
        var window = new MainWindow(vm, app.Get<SettingsStore>());
        window.ShowShortcutsCommand.Execute(null);
        Assert.Equal("settings", vm.SelectedNav!.Key);
        Assert.Equal(SettingsViewModel.ShortcutsTab, vm.Settings.SelectedTab);
    });

}
