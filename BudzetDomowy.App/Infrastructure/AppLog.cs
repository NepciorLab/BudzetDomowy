using BudzetDomowy.Core.Logging;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>The program's log: %LocalAppData%\BudzetDomowy[\Profiles\X]\logs, one file per day, two weeks kept.</summary>
public static class AppLog
{
    private static readonly Lazy<FileLog> Log = new(() => new FileLog(AppPaths.LogDir));

    public static FileLog Instance => Log.Value;

    public static string Directory => Instance.Directory;

    public static void Info(string message) => Instance.Info(message);
    public static void Warn(string message) => Instance.Warn(message);
    public static void Error(string message, Exception? exception = null) => Instance.Error(message, exception);
}
