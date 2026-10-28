using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>One page of the help: a node of the tree of topics.</summary>
public sealed partial class HelpTopic : ObservableObject
{
    public HelpTopic(string id, string title, string markup)
    {
        Id = id;
        Title = title;
        Markup = markup;
    }

    public string Id { get; }
    public string Title { get; }

    /// <summary>The text of the page in the help markup (see <see cref="HelpDocumentBuilder"/>).</summary>
    public string Markup { get; }

    public HelpTopic? Parent { get; private set; }
    public List<HelpTopic> Children { get; } = [];

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;

    internal void Add(HelpTopic child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public IEnumerable<HelpTopic> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants()) yield return d;
        }
    }
}

/// <summary>
/// The tree of the help. The texts are files in <c>Help\</c> (embedded in the program): <c>index.txt</c> lists the topic ids, one per line,
/// two spaces of indentation per level; <c>&lt;id&gt;.md</c> is the page, whose first line is <c>#</c> and the title.
/// </summary>
public static class HelpTopics
{
    private static readonly Assembly Source = typeof(HelpTopics).Assembly;

    /// <summary>The ids in the order of the tree, with the depth of each (0 = top).</summary>
    public static IReadOnlyList<(string Id, int Depth)> Index()
    {
        var list = new List<(string, int)>();
        foreach (var raw in (ReadText("index.txt") ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var depth = (line.Length - line.TrimStart().Length) / 2;
            list.Add((line.Trim(), depth));
        }

        return list;
    }

    public static string? ReadText(string file)
    {
        using var stream = Source.GetManifestResourceStream("Help/" + file);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The first line of a page is its title: <c># Title</c>.</summary>
    public static string TitleOf(string markup)
    {
        var first = markup.Split('\n')[0].Trim();
        return first.StartsWith("# ", StringComparison.Ordinal) ? first[2..].Trim() : first;
    }

    /// <summary>A new tree (every help window has its own selection and expansion). Topics without a page are left out.</summary>
    public static IReadOnlyList<HelpTopic> Load()
    {
        var roots = new List<HelpTopic>();
        var stack = new List<HelpTopic>();
        foreach (var (id, depth) in Index())
        {
            var markup = ReadText(id + ".md");
            if (markup is null) continue;
            var topic = new HelpTopic(id, TitleOf(markup), markup.Replace("\r\n", "\n"));
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(topic); else stack[^1].Add(topic);
            stack.Add(topic);
        }

        return roots;
    }

    public static IEnumerable<HelpTopic> Flatten(IEnumerable<HelpTopic> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var d in root.Descendants()) yield return d;
        }
    }

    /// <summary>The topic with this id; when there is none, the nearest one above it ("a.b.c" falls back to "a.b", then "a").</summary>
    public static HelpTopic? Find(IEnumerable<HelpTopic> roots, string? id)
    {
        var all = Flatten(roots).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(id))
        {
            if (all.TryGetValue(id, out var found)) return found;
            var dot = id.LastIndexOf('.');
            id = dot < 0 ? null : id[..dot];
        }

        return null;
    }
}
