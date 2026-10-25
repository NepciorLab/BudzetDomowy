using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.Infrastructure.Updates;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.UiTests;

/// <summary>Version 0.4.1: data next to the program, an update instead of an error for newer data, the clock.</summary>
public class V41UiTests
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

    // ----- data next to the program -----

    [Fact]
    public void Paths_SettingsLogsAndDataLiveNextToTheProgram_NotInAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(AppPaths.ProgramDir, AppPaths.SettingsDir, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Data", "Profiles", "UiTests"), AppPaths.SettingsDir);       // the tests use their own profile
        Assert.DoesNotContain(Path.Combine(appData, "BudzetDomowy"), AppPaths.SettingsDir, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppPaths.SettingsDir, "logs"), AppPaths.LogDir);
        Assert.Equal(Path.Combine(AppPaths.SettingsDir, "settings.json"), AppPaths.SettingsFile);
        Assert.Equal(AppPaths.SettingsDir, AppPaths.LocalDir);                                    // database and device identity too
        Assert.True(AppPaths.IsWritable(AppPaths.SettingsDir));
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "budzet-paths-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Paths_DataOfAnOlderVersionIsCopiedOnce_KeepingTheDeviceIdentity()
    {
        var legacy = TempDir();
        var target = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            Directory.CreateDirectory(Path.Combine(legacy, "Backups"));
            Directory.CreateDirectory(Path.Combine(legacy, "Profiles", "X"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"DataFolder\":\"G:\\\\OneDrive\\\\x\"}");
            File.WriteAllText(Path.Combine(legacy, "device.json"), "{\"DeviceId\":\"d6fad295\"}");
            File.WriteAllText(Path.Combine(legacy, "budzet.db"), "db");
            File.WriteAllText(Path.Combine(legacy, "import-mapping.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "logs", "a.log"), "log");
            File.WriteAllText(Path.Combine(legacy, "Backups", "backup_2026-10-20_040000_auto.zip"), "zip");
            File.WriteAllText(Path.Combine(legacy, "Profiles", "X", "settings.json"), "other profile");

            var result = AppPaths.CopyLegacyData(legacy, target);

            Assert.NotNull(result);
            Assert.Equal("{\"DeviceId\":\"d6fad295\"}", File.ReadAllText(Path.Combine(target, "device.json")));   // the same computer in the shared folder
            Assert.True(File.Exists(Path.Combine(target, "settings.json")));
            Assert.True(File.Exists(Path.Combine(target, "budzet.db")));
            Assert.True(File.Exists(Path.Combine(target, "logs", "a.log")));
            Assert.True(File.Exists(Path.Combine(target, "Backups", "backup_2026-10-20_040000_auto.zip")));
            Assert.False(Directory.Exists(Path.Combine(target, "Profiles")));                                      // profiles move on their own
            Assert.True(File.Exists(Path.Combine(legacy, "device.json")));                                         // the old files stay

            File.WriteAllText(Path.Combine(target, "settings.json"), "changed in the new place");
            Assert.Null(AppPaths.CopyLegacyData(legacy, target));                                                   // the new place has data: never overwritten
            Assert.Equal("changed in the new place", File.ReadAllText(Path.Combine(target, "settings.json")));
        }
        finally
        {
            foreach (var dir in new[] { legacy, target }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Paths_NothingIsCopiedWhenThereIsNoOldData()
    {
        var empty = TempDir();
        var target = TempDir();
        try
        {
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(empty, "unrelated.txt"), "x");
            Assert.Null(AppPaths.CopyLegacyData(empty, target));
            Assert.Null(AppPaths.CopyLegacyData(Path.Combine(empty, "missing"), target));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    // ----- newer data: an update instead of an error -----

    private const string ReleaseUrl = "https://api.github.com/repos/NepciorLab/BudzetDomowy/releases/latest";

    private static byte[] Release(string tag) => Encoding.UTF8.GetBytes(
        $$"""{"tag_name":"{{tag}}","html_url":"u","body":"","assets":[{"name":"BudzetDomowy.exe","url":"a","browser_download_url":"https://github.com/dl/BudzetDomowy.exe","size":0}]}""");

    [Fact]
    public void NewerData_TheUpdateIsOfferedWithTheReason_EvenWhenAutomaticUpdatesAreOff() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var prompt = app.Get<FakeUpdatePrompt>();
        var updates = app.Get<UpdateService>();
        app.Get<SettingsStore>().Update(s => s with { AutoUpdate = false });
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Release("v0.9.0"));

        prompt.Answer = false;
        Assert.False(await updates.OfferUpdateAsync(dataIsNewer: true));
        Assert.Equal(1, prompt.Asked);
        Assert.True(prompt.LastDataIsNewer);                                // the question says why

        await updates.RunAutomaticAsync();                                  // the ordinary check respects the setting
        Assert.Equal(1, prompt.Asked);

        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Release("v0.1.0"));   // nothing newer: no question, the caller shows its message
        Assert.False(await updates.OfferUpdateAsync(dataIsNewer: true));
        Assert.Equal(1, prompt.Asked);

        github.Responses[ReleaseUrl] = (HttpStatusCode.NotFound, []);
        Assert.False(await updates.OfferUpdateAsync(dataIsNewer: true));
    });

    [Fact]
    public void NewerData_TheWatcherOffersTheUpdateOnlyOncePerRun() => WithApp(app =>
    {
        var offers = 0;
        var watcher = new NewerDataWatcher(app.Get<UpdateService>(), work => { offers++; });
        var fine = new SyncStatus();
        var newer = new SyncStatus { Issues = [new SyncIssue(SyncIssueKind.UpdateRequired, "journal_x.jsonl", "uses a newer schema")] };

        watcher.Observe(fine);
        Assert.Equal(0, offers);
        watcher.Observe(newer);
        watcher.Observe(newer);
        Assert.Equal(1, offers);
    });

    [Fact]
    public void NewerData_TheWatcherRunsTheOfferThroughTheUpdateService() => WithAppAsync(async app =>
    {
        var github = app.Get<FakeGitHub>();
        var prompt = app.Get<FakeUpdatePrompt>();
        github.Responses[ReleaseUrl] = (HttpStatusCode.OK, Release("v0.9.0"));
        Func<Task>? scheduled = null;
        var watcher = new NewerDataWatcher(app.Get<UpdateService>(), work => scheduled = work);

        watcher.Observe(new SyncStatus { Issues = [new SyncIssue(SyncIssueKind.UpdateRequired, "journal_x.jsonl", "newer")] });
        Assert.NotNull(scheduled);
        await scheduled!();

        Assert.Equal(1, prompt.Asked);
        Assert.True(prompt.LastDataIsNewer);
    });

    // ----- the clock -----

    [Fact]
    public void Clock_ShowsTheTimeBesideTheDate_AndCanBeSwitchedOff() => WithApp(app =>
    {
        var prefs = UiPreferences.Instance;
        prefs.Initialize(app.Get<SettingsStore>());
        try
        {
            var main = app.Get<MainViewModel>();
            Assert.Matches(@"^\d{2}:\d{2}$", main.ClockText);
            Assert.True(prefs.ShowClock);                                       // on by default

            var window = new MainWindow(main, app.Get<SettingsStore>());
            var label = Assert.IsType<TextBlock>(window.FindName("ClockLabel"));
            Assert.Equal(Visibility.Visible, label.Visibility);

            prefs.ShowClock = false;
            Assert.Equal(Visibility.Collapsed, label.Visibility);
            Assert.False(app.Get<SettingsStore>().Current.ShowClock);           // remembered

            prefs.ShowClock = true;
            Assert.Equal(Visibility.Visible, label.Visibility);
        }
        finally
        {
            prefs.ShowClock = true;
        }
    });

    [Fact]
    public void Clock_TheOptionIsInTheAppearanceTab() => WithApp(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        vm.SelectedTab = SettingsViewModel.AppearanceTab;
        var view = new SettingsView { DataContext = vm };
        UiHost.Layout(view, 1100, 800);
        Assert.Contains("Pokazuj zegar obok daty", UiHost.Descendants<CheckBox>(view).Select(c => c.Content?.ToString()));
    });
}
