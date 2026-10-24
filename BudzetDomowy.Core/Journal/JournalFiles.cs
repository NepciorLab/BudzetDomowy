using System.Text.RegularExpressions;

namespace BudzetDomowy.Core.Journal;

public static class JournalFiles
{
    public const string Prefix = "journal_";
    public const string Extension = ".jsonl";

    private static readonly Regex ValidName = new(
        @"^journal_(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string FileNameFor(string deviceId) => $"{Prefix}{deviceId}{Extension}";

    public static string PathFor(string folder, string deviceId) => Path.Combine(folder, FileNameFor(deviceId));

    /// <summary>True for a regular device journal name; the device id is returned lowercase.</summary>
    public static bool TryGetDeviceId(string fileName, out string deviceId)
    {
        var m = ValidName.Match(fileName);
        deviceId = m.Success ? m.Groups["id"].Value.ToLowerInvariant() : "";
        return m.Success;
    }

    /// <summary>
    /// A file that looks like a journal but is not a regular one, e.g. OneDrive conflict copies such as
    /// "journal_&lt;id&gt;-DESKTOP-ABC.jsonl" or "journal_&lt;id&gt; (1).jsonl".
    /// </summary>
    public static bool LooksLikeConflictCopy(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && fileName.Contains(".jsonl", StringComparison.OrdinalIgnoreCase)
        && !ValidName.IsMatch(fileName);
}
