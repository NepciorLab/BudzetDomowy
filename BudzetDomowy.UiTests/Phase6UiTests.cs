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
    public void BackgroundMode_StartsAndStopsTheHost_AndRemembersTheChoice() => WithApp(app =>
    {
        var host = (FakeHost)app.Services.GetService(typeof(IBackgroundHost))!;
        var vm = app.Get<SettingsViewModel>();
        var store = app.Get<SettingsStore>();
        Assert.False(vm.BackgroundMode);

        vm.BackgroundMode = true;
        Assert.True(host.IsActive);
        Assert.True(store.Current.BackgroundMode);

        vm.BackgroundMode = false;
        Assert.False(host.IsActive);
        Assert.False(store.Current.BackgroundMode);
    });

    [Fact]
    public void BackgroundMode_WhenTheShortcutIsTaken_SwitchesBackAndExplainsWhy() => WithApp(app =>
    {
        var host = (FakeHost)app.Services.GetService(typeof(IBackgroundHost))!;
        host.Refuse = true;
        var vm = app.Get<SettingsViewModel>();

        vm.BackgroundMode = true;

        Assert.False(vm.BackgroundMode);
        Assert.False(host.IsActive);
        Assert.False(app.Get<SettingsStore>().Current.BackgroundMode);
        Assert.False(string.IsNullOrEmpty(vm.BackgroundMessage));
    });

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
        Assert.Contains("0.3.1", vm.Version);

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
        Assert.Contains("Ctrl + Alt + B", keys);
        Assert.Contains("F1", keys);
        Assert.Equal(23, vm.Shortcuts.Count);
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

    [Fact]
    public void QuickAddWindow_CanBeBuilt_WithoutBeingShown() => WithApp(app =>
    {
        var window = new QuickAddWindow(app.Get<QuickAddViewModel>());
        Assert.False(window.IsVisible);
        Assert.NotNull(window.Content);
    });

    [Fact]
    public void QuickAdd_EscClearsTheFields() => WithApp(app =>
    {
        var vm = app.Get<QuickAddViewModel>();
        vm.AmountText = "12,5";
        vm.Place = "Lewiatan";
        vm.ClearCommand.Execute(null);
        Assert.Equal("", vm.AmountText);
        Assert.Equal("", vm.Place);
    });
}
