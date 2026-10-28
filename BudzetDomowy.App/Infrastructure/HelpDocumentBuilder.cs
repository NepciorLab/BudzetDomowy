using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Turns the text of a help page into a document. The markup is a small part of Markdown:
/// <code>
/// # Title                      first line of every page
/// ## Heading     ### Smaller heading
/// - item         (a list; a line indented by two spaces continues the item)
/// 1. step        (a numbered list)
/// &gt; note        (a box with a tip; consecutive lines make one box)
/// ![caption](picture.png)       a picture from Assets\Help, alone on its line
/// **bold**   `Ctrl+K` (a key)   {key:Entry.Expense} (the key chosen now in Settings)   [text](topic.id) (a link)
/// </code>
/// </summary>
public static class HelpDocumentBuilder
{
    public const string PictureFolder = "pack://application:,,,/BudzetDomowy.App;component/Assets/Help/";

    private static readonly Regex Inline = new(
        @"\*\*(?<bold>.+?)\*\*|`(?<key>.+?)`|\{key:(?<id>[A-Za-z.]+)\}|\[(?<text>[^\]]+)\]\((?<topic>[A-Za-z0-9._-]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex Picture = new(@"^!\[(?<caption>[^\]]*)\]\((?<file>[^)]+)\)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\d+\.\s+", RegexOptions.Compiled);

    /// <summary>The names of the pictures a page uses (for checks).</summary>
    public static IEnumerable<string> PicturesIn(string markup) =>
        markup.Split('\n').Select(l => Picture.Match(l.Trim())).Where(m => m.Success).Select(m => m.Groups["file"].Value);

    /// <summary>The ids of the topics a page links to (for checks).</summary>
    public static IEnumerable<string> LinksIn(string markup) =>
        Inline.Matches(markup).Where(m => m.Groups["topic"].Success && !m.Groups["topic"].Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["topic"].Value);

    /// <summary>The ids of the keys a page mentions as {key:...} (for checks).</summary>
    public static IEnumerable<string> KeysIn(string markup) =>
        Inline.Matches(markup).Where(m => m.Groups["id"].Success).Select(m => m.Groups["id"].Value);

    public static FlowDocument Build(HelpTopic topic, Action<string> openTopic)
    {
        var text = Brush("SystemControlPageTextBaseHighBrush", Brushes.Black);
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = text,
            PagePadding = new Thickness(28, 20, 28, 28),
            LineHeight = 21,
            TextAlignment = TextAlignment.Left,
        };

        var lines = topic.Markup.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                doc.Blocks.Add(Heading(trimmed[2..], 28, new Thickness(0, 0, 0, 10), FontWeights.SemiBold, openTopic));
                i++;
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                doc.Blocks.Add(Heading(trimmed[3..], 19, new Thickness(0, 18, 0, 6), FontWeights.SemiBold, openTopic));
                i++;
            }
            else if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                doc.Blocks.Add(Heading(trimmed[4..], 15, new Thickness(0, 12, 0, 4), FontWeights.Bold, openTopic));
                i++;
            }
            else if (Picture.Match(trimmed) is { Success: true } picture)
            {
                doc.Blocks.Add(PictureBlock(picture.Groups["file"].Value, picture.Groups["caption"].Value));
                i++;
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || Numbered.IsMatch(trimmed))
            {
                var ordered = Numbered.IsMatch(trimmed);
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 2, 0, 8),
                    Padding = new Thickness(26, 0, 0, 0),
                };
                while (i < lines.Count && (lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal) || Numbered.IsMatch(lines[i].Trim())))
                {
                    var item = Numbered.Replace(lines[i].Trim().StartsWith("- ", StringComparison.Ordinal) ? lines[i].Trim()[2..] : lines[i].Trim(), "");
                    i++;
                    while (i < lines.Count && lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0) item += " " + lines[i++].Trim();
                    var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInline(paragraph.Inlines, item, openTopic);
                    list.ListItems.Add(new ListItem(paragraph));
                }

                doc.Blocks.Add(list);
            }
            else if (trimmed.StartsWith('>'))
            {
                var parts = new List<string>();
                while (i < lines.Count && lines[i].Trim().StartsWith('>')) parts.Add(lines[i++].Trim().TrimStart('>').Trim());
                doc.Blocks.Add(NoteBlock(string.Join(" ", parts), openTopic));
            }
            else
            {
                var parts = new List<string>();
                while (i < lines.Count && lines[i].Trim().Length > 0 && !IsSpecial(lines[i].Trim())) parts.Add(lines[i++].Trim());
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInline(paragraph.Inlines, string.Join(" ", parts), openTopic);
                doc.Blocks.Add(paragraph);
            }
        }

        return doc;
    }

    private static bool IsSpecial(string line) =>
        line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith('>')
        || Numbered.IsMatch(line) || Picture.IsMatch(line);

    private static Paragraph Heading(string text, double size, Thickness margin, FontWeight weight, Action<string> open)
    {
        var paragraph = new Paragraph { FontSize = size, FontWeight = weight, Margin = margin, LineHeight = double.NaN };
        AddInline(paragraph.Inlines, text, open);
        return paragraph;
    }

    private static Block NoteBlock(string text, Action<string> open)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        AddInline(paragraph.Inlines, text, open);
        return new Section(paragraph)
        {
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush("SystemControlForegroundAccentBrush", Brushes.SteelBlue),
            Background = Brush("SystemControlBackgroundChromeMediumLowBrush", Brushes.WhiteSmoke),
        };
    }

    private static Block PictureBlock(string file, string caption)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        UIElement content;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(PictureFolder + file, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            content = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("SystemControlForegroundBaseLowBrush", Brushes.LightGray),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, SnapsToDevicePixels = true },
            };
            RenderOptions.SetBitmapScalingMode(((Border)content).Child, BitmapScalingMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or InvalidOperationException)
        {
            content = new TextBlock { Text = "[" + file + "]", Foreground = Brushes.IndianRed };
        }

        panel.Children.Add(content);
        if (caption.Length > 0)
            panel.Children.Add(new TextBlock { Text = caption, Opacity = 0.7, FontStyle = FontStyles.Italic, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return new BlockUIContainer(panel);
    }

    private static void AddInline(InlineCollection inlines, string text, Action<string> openTopic)
    {
        var position = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > position) inlines.Add(new Run(text[position..m.Index]));
            position = m.Index + m.Length;

            if (m.Groups["bold"].Success) inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            else if (m.Groups["key"].Success) inlines.Add(KeyCap(m.Groups["key"].Value));
            else if (m.Groups["id"].Success) inlines.Add(KeyCap(ShortcutKeyText(m.Groups["id"].Value)));
            else
            {
                var topic = m.Groups["topic"].Value;
                var link = new Hyperlink(new Run(m.Groups["text"].Value))
                {
                    Foreground = Brush("SystemControlForegroundAccentBrush", Brushes.SteelBlue),
                    TextDecorations = null,
                    ToolTip = topic,
                };
                link.Click += (_, _) => openTopic(topic);
                inlines.Add(link);
            }
        }

        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    /// <summary>The key as it is now (it may have been changed in Settings); an unknown id is shown as it is.</summary>
    private static string ShortcutKeyText(string id) =>
        ShortcutMap.Definitions.Any(d => d.Id == id) ? ShortcutMap.Instance.DisplayOf(id) : id;

    private static Run KeyCap(string keys) => new(" " + keys.Replace("+", " + ").Replace("  ", " ") + " ")
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Background = Brush("SystemControlBackgroundBaseLowBrush", Brushes.LightGray),
    };

    private static Brush Brush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
