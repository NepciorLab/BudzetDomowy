namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Per-installation folders. Settings, logs, the local database, backups and the device identity all live in the "Data" folder
/// next to the program (so the program can be carried and removed as one folder). Only when that folder cannot be written
/// (e.g. the program sits in "Program Files") the old place in %LocalAppData% is used. Never inside OneDrive.
/// </summary>
public static class AppPaths
{
    /// <summary>Profile name from "--profile X" (lets two instances act as two computers on one machine); null for the normal profile.</summary>
    public static string? Profile { get; private set; }

    public static void Initialize(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                Profile = Sanitize(args[i + 1]);
            else if (args[i].StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                Profile = Sanitize(args[i]["--profile=".Length..]);
        }
    }

    /// <summary>The folder of the running program (of the build output when started through "dotnet").</summary>
    public static string ProgramDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            var host = exe is not null && Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var dir = exe is null || host ? AppContext.BaseDirectory : Path.GetDirectoryName(exe)!;
            return Path.GetFullPath(dir);
        }
    }

    /// <summary>The old place of the data, used for a program that cannot write next to itself and as the source of the one-time move.</summary>
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BudzetDomowy");

    private static string? _dataRoot;

    public static string DataRoot => _dataRoot ??= ResolveDataRoot();

    private static string ResolveDataRoot()
    {
        var beside = Path.Combine(ProgramDir, "Data");
        return IsWritable(beside) ? beside : LegacyRoot;
    }

    public static bool IsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Where settings.json and the logs live: fixed, so the program can always find them before it knows anything else.</summary>
    public static string SettingsDir => Profile is null ? DataRoot : Path.Combine(DataRoot, "Profiles", Profile);

    /// <summary>Where this profile kept its data before it moved next to the program.</summary>
    public static string LegacyDir => Profile is null ? LegacyRoot : Path.Combine(LegacyRoot, "Profiles", Profile);

    private static string? _customLocalDir;

    /// <summary>Local working data (database cache, device identity): the folder chosen in Settings, else <see cref="SettingsDir"/>. Never inside OneDrive.</summary>
    public static string LocalDir => string.IsNullOrWhiteSpace(_customLocalDir) ? SettingsDir : _customLocalDir;

    public static void UseLocalFolder(string? custom) => _customLocalDir = string.IsNullOrWhiteSpace(custom) ? null : Path.GetFullPath(custom);

    public static string LogDir => Path.Combine(SettingsDir, "logs");

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    /// <summary>Copies what the older versions kept in %LocalAppData% to the new place, once. Returns a short description, or null when nothing was copied.</summary>
    public static string? MigrateFromLegacy() =>
        string.Equals(Path.GetFullPath(LegacyDir), Path.GetFullPath(SettingsDir), StringComparison.OrdinalIgnoreCase)
            ? null
            : CopyLegacyData(LegacyDir, SettingsDir);

    private static readonly string[] LegacyFiles =
        ["settings.json", "device.json", "import-mapping.json", "budzet.db", "budzet.db-wal", "budzet.db-shm"];

    private static readonly string[] LegacyFolders = ["logs", "Backups"];

    /// <summary>
    /// Copies the program's own files (settings, device identity, database, import mapping, logs, backups) from an old folder
    /// to a new one that has no data yet. The old files are left where they are; the device identity is kept, so the computer
    /// stays the same device in the shared data folder.
    /// </summary>
    public static string? CopyLegacyData(string legacy, string target)
    {
        if (!Directory.Exists(legacy)) return null;
        if (File.Exists(Path.Combine(target, "settings.json")) || File.Exists(Path.Combine(target, "device.json"))) return null;
        if (!File.Exists(Path.Combine(legacy, "settings.json")) && !File.Exists(Path.Combine(legacy, "device.json"))) return null;

        Directory.CreateDirectory(target);
        var copied = 0;
        foreach (var name in LegacyFiles)
        {
            var from = Path.Combine(legacy, name);
            if (!File.Exists(from)) continue;
            File.Copy(from, Path.Combine(target, name), overwrite: false);
            copied++;
        }
        foreach (var folder in LegacyFolders) copied += CopyFolder(Path.Combine(legacy, folder), Path.Combine(target, folder));
        return $"{copied} file(s) copied from '{legacy}' to '{target}'";
    }

    private static int CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from)) return 0;
        Directory.CreateDirectory(to);
        var count = 0;
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            count++;
        }
        return count;
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return clean.Length == 0 ? "X" : clean;
    }
}
