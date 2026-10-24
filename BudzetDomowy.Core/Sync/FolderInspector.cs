using BudzetDomowy.Core.Journal;

namespace BudzetDomowy.Core.Sync;

public sealed record FolderCheck(
    string Path,
    bool Exists,
    bool Writable,
    bool InOneDrive,
    /// <summary>Device ids of journals already present in the folder.</summary>
    IReadOnlyList<string> JournalDevices,
    int CloudOnlyJournals,
    int ConflictCopies,
    string? Error)
{
    public bool HasExistingData => JournalDevices.Count > 0;
    public bool CanUse => Exists && Writable && Error is null;
}

/// <summary>Checks a candidate data folder before the app starts using it (first-run wizard).</summary>
public static class FolderInspector
{
    public static FolderCheck Inspect(string? path, Func<FileInfo, bool>? isCloudOnly = null)
    {
        isCloudOnly ??= CloudFiles.IsCloudOnly;
        if (string.IsNullOrWhiteSpace(path))
            return new FolderCheck("", false, false, false, [], 0, 0, null);

        var full = System.IO.Path.GetFullPath(path);
        var oneDrive = IsInOneDrive(full);
        if (!Directory.Exists(full))
            return new FolderCheck(full, false, false, oneDrive, [], 0, 0, null);

        try
        {
            var devices = new List<string>();
            var cloud = 0;
            var conflicts = 0;
            foreach (var file in Directory.GetFiles(full, JournalFiles.Prefix + "*"))
            {
                var name = System.IO.Path.GetFileName(file);
                if (JournalFiles.TryGetDeviceId(name, out var id))
                {
                    devices.Add(id);
                    if (isCloudOnly(new FileInfo(file))) cloud++;
                }
                else if (JournalFiles.LooksLikeConflictCopy(name)) conflicts++;
            }
            return new FolderCheck(full, true, CanWrite(full), oneDrive, devices, cloud, conflicts, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FolderCheck(full, true, false, oneDrive, [], 0, 0, e.Message);
        }
    }

    public static bool IsInOneDrive(string fullPath)
    {
        var roots = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(v => !string.IsNullOrWhiteSpace(v));
        foreach (var root in roots)
        {
            var r = System.IO.Path.GetFullPath(root!).TrimEnd('\\') + "\\";
            if (fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return fullPath.Split('\\', '/').Any(p => p.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanWrite(string folder)
    {
        var probe = System.IO.Path.Combine(folder, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
