using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Views;

/// <summary>The help: the tree of topics on the left, the page on the right. It is not modal, so it can stay open beside the program.</summary>
public partial class HelpWindow : Window
{
    private static Rect? _remembered;

    private readonly IReadOnlyList<HelpTopic> _roots = HelpTopics.Load();
    private readonly List<string> _history = [];
    private int _at = -1;
    private bool _moving;

    public HelpWindow()
    {
        InitializeComponent();
        UiScaling.Attach(this);
        Help.SetTopic(this, "help");
        Tree.ItemsSource = _roots;
        foreach (var root in _roots) root.IsExpanded = true;

        if (_remembered is { } r)
        {
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width - 40);
            Height = Math.Min(Height, area.Height - 40);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }

        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _remembered = new Rect(Left, Top, Width, Height);
        };
    }

    /// <summary>The topic now shown (null when there are no pages at all).</summary>
    public HelpTopic? Current { get; private set; }

    public IReadOnlyList<HelpTopic> Roots => _roots;

    /// <summary>Opens the page of the topic, or the nearest one above it; an unknown id shows the first page.</summary>
    public void ShowTopic(string? id)
    {
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        var topic = HelpTopics.Find(_roots, id) ?? _roots.FirstOrDefault();
        if (topic is null) return;
        Go(topic, remember: true);
    }

    private void Go(HelpTopic topic, bool remember)
    {
        if (remember && !(_at >= 0 && _history[_at] == topic.Id))
        {
            _history.RemoveRange(_at + 1, _history.Count - _at - 1);
            _history.Add(topic.Id);
            _at = _history.Count - 1;
        }

        _moving = true;
        try
        {
            foreach (var other in HelpTopics.Flatten(_roots)) other.IsSelected = false;
            for (var parent = topic.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
            topic.IsSelected = true;
        }
        finally { _moving = false; }

        Current = topic;
        Viewer.Document = HelpDocumentBuilder.Build(topic, id => ShowTopic(id));
        Crumbs.Text = string.Join("  ›  ", TrailOf(topic).Select(t => t.Title));
        Title = Res.Get("HelpTitle") + " – " + topic.Title;
        BackButton.IsEnabled = _at > 0;
        ForwardButton.IsEnabled = _at < _history.Count - 1;
        Viewer.ScrollViewer()?.ScrollToTop();
    }

    private static IEnumerable<HelpTopic> TrailOf(HelpTopic topic)
    {
        var path = new List<HelpTopic>();
        for (var t = topic; t is not null; t = t.Parent) path.Insert(0, t);
        return path;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_moving || e.NewValue is not HelpTopic picked) return;
        var real = HelpTopics.Find(_roots, picked.Id);       // a hit of the search is a copy: show the real page
        if (real is not null && real != Current) Go(real, remember: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Forward_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int by)
    {
        var next = _at + by;
        if (next < 0 || next >= _history.Count) return;
        _at = next;
        if (HelpTopics.Find(_roots, _history[_at]) is { } topic) Go(topic, remember: false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            Tree.ItemsSource = _roots;
            return;
        }

        // the hits are a flat list of copies of the pages (the real ones stay in the tree)
        Tree.ItemsSource = HelpTopics.Flatten(_roots)
            .Where(t => t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || t.Markup.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(t => t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(t => new HelpTopic(t.Id, t.Title, ""))
            .ToList();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            else Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Step(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Step(1);
            e.Handled = true;
        }
    }
}

internal static class FlowViewerExtensions
{
    /// <summary>The scroll viewer inside a document viewer (it exists once the viewer has been drawn).</summary>
    public static ScrollViewer? ScrollViewer(this FlowDocumentScrollViewer viewer)
    {
        viewer.ApplyTemplate();
        return viewer.Template?.FindName("PART_ContentHost", viewer) is ScrollViewer scroll ? scroll : null;
    }
}
