namespace BudzetDomowy.App.Infrastructure;

/// <summary>Per-installation folders. Everything here is local and must never be inside OneDrive.</summary>
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

    public static string LocalDir
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BudzetDomowy");
            return Profile is null ? root : Path.Combine(root, "Profiles", Profile);
        }
    }

    public static string SettingsFile => Path.Combine(LocalDir, "settings.json");

    private static string Sanitize(string name)
    {
        var clean = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return clean.Length == 0 ? "X" : clean;
    }
}
