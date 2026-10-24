using System.Text;

namespace BudzetDomowy.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

/// <summary>
/// One text file per day in a local folder (never in OneDrive). Writing never throws: a broken log must not break the program.
/// Files older than <c>keepDays</c> are removed.
/// </summary>
public sealed class FileLog : ILog
{
    private readonly string _directory;
    private readonly Func<DateTime> _now;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private DateOnly _prunedOn;

    public FileLog(string directory, Func<DateTime>? now = null, int keepDays = 14)
    {
        _directory = directory;
        _now = now ?? (() => DateTime.Now);
        _keepDays = keepDays;
    }

    public string Directory => _directory;

    public string FileFor(DateTime day) => Path.Combine(_directory, $"budzet-{day:yyyy-MM-dd}.log");

    public string CurrentFile => FileFor(_now());

    public void Info(string message) => Write("INFO ", message, null);
    public void Warn(string message) => Write("WARN ", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                var now = _now();
                System.IO.Directory.CreateDirectory(_directory);
                var sb = new StringBuilder();
                sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').AppendLine(message);
                for (var e = exception; e is not null; e = e.InnerException)
                {
                    sb.Append("    ").Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
                    if (e.StackTrace is { } trace) sb.AppendLine(trace);
                }
                File.AppendAllText(FileFor(now), sb.ToString(), Encoding.UTF8);

                var today = DateOnly.FromDateTime(now);
                if (_prunedOn != today)
                {
                    _prunedOn = today;
                    Prune(now);
                }
            }
        }
        catch (Exception)
        {
            // logging must never be the reason something fails
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "budzet-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["budzet-".Length..];
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < now.Date.AddDays(-_keepDays))
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
