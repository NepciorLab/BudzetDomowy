using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;

namespace BudzetDomowy.UiTests;

public class HelpTests
{
    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static void WithApp(Action<TestApp> test)
    {
        using var app = new TestApp();
        UiHost.Run(() => test(app));
    }

    /// <summary>The program's sources: the folder that holds BudzetDomowy.sln (the tests run from bin\...).</summary>
    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BudzetDomowy.sln"))) return dir.FullName;
        throw new InvalidOperationException("BudzetDomowy.sln not found above " + AppContext.BaseDirectory);
    }

    // ----- the pages -----

    [Fact]
    public void Pages_EveryTopicOfTheIndexHasAPage_AndEveryPageIsInTheIndex()
    {
        var ids = HelpTopics.Index().Select(i => i.Id).ToList();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());                                // no topic twice

        foreach (var id in ids)
        {
            var markup = HelpTopics.ReadText(id + ".md");
            Assert.True(markup is not null, $"No page for the topic {id}.");
            Assert.StartsWith("# ", markup!);                                           // the title
            Assert.True(HelpTopics.TitleOf(markup).Length > 2, $"No title in {id}.");
        }

        var onDisk = Directory.GetFiles(Path.Combine(SourceRoot(), "BudzetDomowy.App", "Help"), "*.md").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        Assert.Empty(onDisk.Except(ids));                                                // a page nobody can reach
    }

    [Fact]
    public void Pages_TheTreeKeepsTheOrderAndTheDepthOfTheIndex()
    {
        var roots = HelpTopics.Load();
        var flat = HelpTopics.Flatten(roots).Select(t => t.Id).ToList();
        Assert.Equal(HelpTopics.Index().Select(i => i.Id), flat);
        Assert.Equal("intro", roots[0].Id);
        Assert.Equal("entry.expense", HelpTopics.Find(roots, "entry.total")!.Parent!.Id);
        Assert.Equal("entry", HelpTopics.Find(roots, "entry.nonsense")!.Id);              // an unknown part falls back to the nearest page above
        Assert.Null(HelpTopics.Find(roots, "nonsense"));
        Assert.Null(HelpTopics.Find(roots, null));
    }

    [Fact]
    public void Pages_EveryLinkPictureAndKeyPointsAtSomethingThatExists() => WithApp(_ =>
    {
        var topics = HelpTopics.Index().Select(i => i.Id).ToHashSet();
        var keys = ShortcutMap.Definitions.Select(d => d.Id).ToHashSet();
        foreach (var topic in HelpTopics.Flatten(HelpTopics.Load()))
        {
            foreach (var link in HelpDocumentBuilder.LinksIn(topic.Markup)) Assert.True(topics.Contains(link), $"{topic.Id}: the link to {link} leads nowhere.");
            foreach (var key in HelpDocumentBuilder.KeysIn(topic.Markup)) Assert.True(keys.Contains(key), $"{topic.Id}: {{key:{key}}} is not a shortcut.");
            foreach (var picture in HelpDocumentBuilder.PicturesIn(topic.Markup))
            {
                var info = Application.GetResourceStream(new Uri(HelpDocumentBuilder.PictureFolder + picture, UriKind.Absolute));
                Assert.True(info is not null, $"{topic.Id}: the picture {picture} is not in the program.");
            }
        }
    });

    [Fact]
    public void Pages_EveryPageMakesADocument_WithItsHeadingAndNoBrokenPicture() => WithApp(_ =>
    {
        foreach (var topic in HelpTopics.Flatten(HelpTopics.Load()))
        {
            var document = HelpDocumentBuilder.Build(topic, _ => { });
            var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.Contains(topic.Title, text);
            Assert.DoesNotContain("!Key!", text);
            Assert.DoesNotContain("{key:", text);                                          // every key was replaced by the real one
            Assert.DoesNotContain("[", text.Replace("[" + "]", ""));                        // a "[file.png]" is how a missing picture shows
        }
    });

    [Fact]
    public void Document_ShowsTheKeysChosenNow_AndLinksOpenTheirTopic() => WithApp(app =>
    {
        var map = ShortcutMap.Instance;
        map.Initialize(app.Get<SettingsStore>());
        try
        {
            var topic = new HelpTopic("t", "Probne", "# Probne\n\nNaciśnij {key:Entry.Expense} albo zobacz [inne](entry.total).\n\n- punkt z `Ctrl+K`\n\n> uwaga **ważna**\n");
            string? opened = null;
            var document = HelpDocumentBuilder.Build(topic, id => opened = id);
            var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.Contains("W", text);
            Assert.Contains("Ctrl + K", text);

            Assert.Null(map.Set(ShortcutMap.NewExpense, Key.E, ModifierKeys.None));
            var changed = HelpDocumentBuilder.Build(topic, _ => { });
            var again = new TextRange(changed.ContentStart, changed.ContentEnd).Text;
            Assert.Contains(" E ", again);

            var link = document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Hyperlink>().Single();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal("entry.total", opened);
        }
        finally { foreach (var d in ShortcutMap.Definitions) map.Reset(d.Id); }
    });

    // ----- the topics of the screens -----

    [Fact]
    public void Screens_EveryHelpTopicInTheSourcesIsAPageOfTheHelp()
    {
        var topics = HelpTopics.Index().Select(i => i.Id).ToHashSet();
        var root = Path.Combine(SourceRoot(), "BudzetDomowy.App");
        var used = new List<(string File, string Topic)>();
        foreach (var file in Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Help\\.Topic=\"([^\"]+)\"")) used.Add((Path.GetFileName(file), m.Groups[1].Value));
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Help\\.SetTopic\\([^,]+,\\s*\"([^\"]+)\"")) used.Add((Path.GetFileName(file), m.Groups[1].Value));

        Assert.True(used.Count > 60, "Too few marked places: " + used.Count);
        foreach (var (file, topic) in used) Assert.True(topics.Contains(topic), $"{file}: the topic {topic} has no page.");

        // a topic must also be reachable: every page of a screen is the topic of something on it (the roots of the other pages are linked)
        var marked = used.Select(u => u.Topic).ToHashSet();
        foreach (var screen in new[] { "dashboard", "transactions", "budgets", "events", "recurring", "payments", "reports", "settings", "entry.expense", "entry.income" })
            Assert.Contains(screen, marked);
    }

    [Fact]
    public void Topic_IsTakenFromTheElementOrTheNearestParentThatHasOne()
    {
        UiHost.Run(() =>
        {
            var button = new Button();
            var inner = new StackPanel { Children = { button } };
            var outer = new Border { Child = inner };
            Assert.Null(Help.TopicOf(button));
            Help.SetTopic(outer, "transactions");
            Assert.Equal("transactions", Help.TopicOf(button));
            Help.SetTopic(button, "transactions.export");
            Assert.Equal("transactions.export", Help.TopicOf(button));
            Assert.Equal("transactions", Help.TopicOf(inner));
            Assert.Null(Help.TopicOf(null));
        });
    }

    // ----- the keys -----

    [Fact]
    public void Keys_F1IsTheHelp_AndF2TheListOfShortcuts() => WithApp(app =>
    {
        var map = ShortcutMap.Instance;
        Assert.Equal("F1", map.KeysOf(ShortcutMap.Help));
        Assert.Equal("F2", map.KeysOf(ShortcutMap.ShortcutsList));
        Assert.Equal("Pomoc: opis zaznaczonego pola albo ekranu", ShortcutMap.Describe(map.Definition(ShortcutMap.Help)));
        Assert.Equal("Pokaż tę listę skrótów", ShortcutMap.Describe(map.Definition(ShortcutMap.ShortcutsList)));

        var window = new MainWindow(app.Get<MainViewModel>(), app.Get<SettingsStore>());
        bool Has(Key key) => window.InputBindings.OfType<KeyBinding>().Any(b => b.Key == key && b.Modifiers == ModifierKeys.None);
        Assert.True(Has(Key.F2));                                                         // the list of shortcuts is a binding of the main window …
        Assert.False(Has(Key.F1));                                                        // … the help is handled for the whole program (also in the entry windows)
        window.ShowShortcutsCommand.Execute(null);
        Assert.Equal("settings", app.Get<MainViewModel>().SelectedNav!.Key);
        Assert.Equal(SettingsViewModel.ShortcutsTab, app.Get<MainViewModel>().Settings.SelectedTab);
    });

    private static KeyEventArgs Press(UIElement target, Key key, RoutedEvent route)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = route, Source = target };
        target.RaiseEvent(e);
        return e;
    }

    [Fact]
    public void F1_OpensTheHelpAtTheScreenThatIsOpen_AndEscDoesNotMeanHelp() => WithApp(app =>
    {
        var topics = new List<string?>();
        HelpService.Install();
        HelpService.ShowHook = topics.Add;
        HelpService.PageTopic = () => "budgets";
        ShortcutMap.Instance.Initialize(app.Get<SettingsStore>());
        try
        {
            var field = new TextBox();
            var screen = new StackPanel { Children = { field } };
            Press(field, Key.Escape, Keyboard.KeyDownEvent);
            Assert.Empty(topics);

            var handled = Press(field, Key.F1, Keyboard.KeyDownEvent);
            Assert.True(handled.Handled);
            Assert.Equal(["budgets"], topics);                                            // nothing has the focus here: the open screen

            var other = new TextBox();
            var again = Press(other, Key.F1, Keyboard.PreviewKeyDownEvent);              // the preview is not the key: a field may take F1 first
            Assert.False(again.Handled);
            Assert.Single(topics);

            Assert.Null(ShortcutMap.Instance.Set(ShortcutMap.Help, Key.F9, ModifierKeys.None));
            Press(field, Key.F1, Keyboard.KeyDownEvent);
            Assert.Single(topics);                                                        // F1 is free now
            Press(field, Key.F9, Keyboard.KeyDownEvent);
            Assert.Equal(2, topics.Count);
        }
        finally
        {
            HelpService.ShowHook = null;
            HelpService.PageTopic = null;
            foreach (var d in ShortcutMap.Definitions) ShortcutMap.Instance.Reset(d.Id);
        }
    });

    // ----- the question mark -----

    private static void Click(UIElement target, MouseButton button = MouseButton.Left)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = target };
        target.RaiseEvent(e);
        Assert.NotNull(e);
        LastClick = e;
    }

    private static MouseButtonEventArgs? LastClick;

    [Fact]
    public void QuestionMark_TurnsTheMouseIntoAHelpPointer_AndTheNextClickOpensTheHelpOfWhatWasClicked() => WithApp(app =>
    {
        var topics = new List<string?>();
        HelpService.Install();
        HelpService.ShowHook = topics.Add;
        HelpService.PageTopic = () => "dashboard";
        try
        {
            var clicked = false;
            var button = new Button();
            button.Click += (_, _) => clicked = true;
            var filters = new StackPanel { Children = { button } };
            Help.SetTopic(filters, "transactions.filters");

            Click(button);
            Assert.False(LastClick!.Handled);                                             // normal: the click is for the button
            Assert.Empty(topics);
            Assert.Null(Mouse.OverrideCursor);

            HelpService.StartPicking();
            Assert.True(HelpService.IsPicking);
            Assert.Same(Cursors.Help, Mouse.OverrideCursor);                              // the arrow with a question mark

            Click(button);
            Assert.True(LastClick!.Handled);                                              // the click does not press the button
            Assert.False(clicked);
            Assert.Equal(["transactions.filters"], topics);
            Assert.False(HelpService.IsPicking);                                          // one click, then back to normal
            Assert.Null(Mouse.OverrideCursor);

            Click(button);
            Assert.False(LastClick!.Handled);                                             // as before the question mark
            Assert.Single(topics);

            HelpService.StartPicking();                                                   // something without a topic: the open screen
            Click(new Button());
            Assert.Equal(["transactions.filters", "dashboard"], topics);
        }
        finally
        {
            HelpService.StopPicking();
            HelpService.ShowHook = null;
            HelpService.PageTopic = null;
        }
    });

    [Fact]
    public void QuestionMark_EscLeavesTheMode_AndTheQuestionMarkButtonAgainToo() => WithApp(app =>
    {
        var topics = new List<string?>();
        HelpService.Install();
        HelpService.ShowHook = topics.Add;
        try
        {
            var field = new TextBox();
            HelpService.StartPicking();
            var esc = Press(field, Key.Escape, Keyboard.PreviewKeyDownEvent);
            Assert.True(esc.Handled);
            Assert.False(HelpService.IsPicking);
            Assert.Null(Mouse.OverrideCursor);
            Assert.Empty(topics);

            var next = Press(field, Key.Escape, Keyboard.PreviewKeyDownEvent);              // outside the mode Esc is not ours
            Assert.False(next.Handled);

            var question = new Button();
            Help.SetTopic(question, "help.button");
            HelpService.StartPicking();
            Click(question);
            Assert.False(HelpService.IsPicking);
            Assert.Empty(topics);                                                          // no help for the button: it only ends the mode
        }
        finally
        {
            HelpService.StopPicking();
            HelpService.ShowHook = null;
        }
    });

    [Fact]
    public void MainWindow_HasTheQuestionMarkButtonAtTheBottomOfTheList_AndItFollowsTheMode() => WithApp(app =>
    {
        var window = new MainWindow(app.Get<MainViewModel>(), app.Get<SettingsStore>());
        var content = (FrameworkElement)window.Content;
        UiHost.Layout(content, 1180, 760);
        var button = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(window.FindName("HelpButton"));
        Assert.Equal("?", button.Content);
        Assert.Equal("help.button", Help.GetTopic(button));
        var nav = (FrameworkElement)window.FindName("Nav")!;
        var origin = button.TransformToAncestor(content).Transform(new Point(0, 0));
        Assert.True(origin.X < 80, "the button is on the left");
        Assert.True(origin.Y > nav.TransformToAncestor(content).Transform(new Point(0, 0)).Y + 300, "the button is at the bottom");

        try
        {
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(HelpService.IsPicking);
            Assert.True(button.IsChecked);
            HelpService.StopPicking();
            Assert.False(button.IsChecked);                                                // the button is released with the mode
        }
        finally { HelpService.StopPicking(); }
    });

    // ----- the window -----

    [Fact]
    public void Window_ShowsTheTopicAndItsPlaceInTheTree_AndGoesBackAndForward() => WithApp(app =>
    {
        var window = new HelpWindow();
        window.ShowTopic("entry.total");
        Assert.Equal("entry.total", window.Current!.Id);
        Assert.True(window.Current.IsSelected);
        Assert.True(window.Current.Parent!.IsExpanded);                                  // the way to it is open
        Assert.Contains("Kwota łączna", window.Title);
        Assert.Contains("Wpisywanie wydatków i przychodów  ›  Nowy wydatek  ›  Kwota łączna", ((TextBlock)window.FindName("Crumbs")!).Text.Replace("  ", "  "));

        window.ShowTopic("entry.nonsense");                                              // unknown: the nearest above
        Assert.Equal("entry", window.Current.Id);
        window.ShowTopic("nonsense");                                                    // no such page at all: the first one
        Assert.Equal("intro", window.Current.Id);
        window.ShowTopic(null);
        Assert.Equal("intro", window.Current.Id);

        var back = (Button)window.FindName("BackButton")!;
        var forward = (Button)window.FindName("ForwardButton")!;
        Assert.True(back.IsEnabled);
        Assert.False(forward.IsEnabled);
        back.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("entry", window.Current.Id);
        back.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("entry.total", window.Current.Id);
        Assert.True(forward.IsEnabled);
        forward.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("entry", window.Current.Id);
        window.Close();
    });

    [Fact]
    public void Window_SearchListsThePagesWithTheWord_AndAnEmptyFieldBringsTheTreeBack() => WithApp(app =>
    {
        var window = new HelpWindow();
        var tree = (TreeView)window.FindName("Tree")!;
        var search = (TextBox)window.FindName("SearchBox")!;
        Assert.Same(window.Roots, tree.ItemsSource);

        search.Text = "kod QR";
        var hits = Assert.IsAssignableFrom<IEnumerable<HelpTopic>>(tree.ItemsSource).ToList();
        Assert.Contains(hits, t => t.Id == "payments.pay");
        Assert.All(hits, t => Assert.Empty(t.Children));                                 // a flat list
        Assert.DoesNotContain(hits, t => t.Id == "budgets");

        search.Text = "";
        Assert.Same(window.Roots, tree.ItemsSource);
        search.Text = "zzzzzz-nic-takiego";
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<HelpTopic>>(tree.ItemsSource));
        window.Close();
    });

    [Fact]
    public void Window_Builds_AndItsOwnTopicIsTheHelp() => WithApp(app =>
    {
        var window = new HelpWindow();
        window.ShowTopic("help");
        Assert.Equal("help", Help.GetTopic(window));
        var viewer = (FlowDocumentScrollViewer)window.FindName("Viewer")!;
        Assert.NotNull(viewer.Document);
        Assert.Contains("Jak korzystać z pomocy", new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text);
        window.Close();
    });

    [Fact]
    public void ScreenTopic_FollowsTheOpenScreenAndTheTabOfSettingsAndReports() => WithApp(app =>
    {
        var main = app.Get<MainViewModel>();
        var window = new MainWindow(main, app.Get<SettingsStore>());
        var topic = HelpService.PageTopic!;
        main.Navigate("transactions");
        Assert.Equal("transactions", topic());
        main.Navigate("settings");
        main.Settings.SelectedTab = SettingsViewModel.ShortcutsTab;
        Assert.Equal("settings.shortcuts", topic());
        main.Settings.SelectedTab = 1;
        Assert.Equal("settings.appearance", topic());
        main.Navigate("reports");
        ((ReportsViewModel)main.CurrentPage!).Tab = ReportTab.Places;
        Assert.Equal("reports.places", topic());
        ((ReportsViewModel)main.CurrentPage!).Tab = ReportTab.Charts;
        Assert.Equal("reports.charts", topic());
        var all = HelpTopics.Index().Select(i => i.Id).ToHashSet();
        foreach (var key in new[] { "dashboard", "transactions", "budgets", "events", "recurring", "payments", "reports", "settings" })
        {
            main.Navigate(key);
            Assert.Contains(topic()!, all);                                               // every screen has its page
        }
    });
}
