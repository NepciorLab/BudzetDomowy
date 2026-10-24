using System.Text;

namespace BudzetDomowy.Core.Journal;

/// <summary>Appends events to this device's own journal. The only writer of that file.</summary>
public sealed class JournalWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();

    public string Path { get; }

    public JournalWriter(string folder, string deviceId)
    {
        Directory.CreateDirectory(folder);
        Path = JournalFiles.PathFor(folder, deviceId);
    }

    /// <summary>Appends one line per event in a single write. Returns the file length before and after.</summary>
    public (long StartOffset, long EndOffset) Append(IReadOnlyList<JournalEvent> events)
    {
        if (events.Count == 0)
        {
            var len = File.Exists(Path) ? new FileInfo(Path).Length : 0;
            return (len, len);
        }

        var sb = new StringBuilder();
        foreach (var e in events) sb.Append(JournalJson.Serialize(e)).Append('\n');
        var bytes = Utf8.GetBytes(sb.ToString());

        lock (_gate)
        {
            return FileRetry.Run(() =>
            {
                using var fs = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                var start = fs.Length;
                fs.Seek(0, SeekOrigin.End);
                if (start > 0 && !EndsWithNewline(fs))
                {
                    // A previous crash left an unterminated line: close it so the new events start on a fresh line.
                    fs.WriteByte((byte)'\n');
                    start = fs.Length - 1;
                }
                fs.Write(bytes);
                fs.Flush(true);
                return (start, fs.Length);
            });
        }
    }

    public (long StartOffset, long EndOffset) Append(JournalEvent e) => Append([e]);

    private static bool EndsWithNewline(FileStream fs)
    {
        // FileAccess.Write cannot read, so peek through a second read-only handle.
        using var peek = new FileStream(fs.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (peek.Length == 0) return true;
        peek.Seek(-1, SeekOrigin.End);
        return peek.ReadByte() == '\n';
    }
}

internal static class FileRetry
{
    public static T Run<T>(Func<T> action, int attempts = 5, int delayMs = 100)
    {
        for (var i = 1; ; i++)
        {
            try { return action(); }
            catch (IOException) when (i < attempts) { Thread.Sleep(delayMs * i); }
            catch (UnauthorizedAccessException) when (i < attempts) { Thread.Sleep(delayMs * i); }
        }
    }
}
