using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudzetDomowy.App.Infrastructure;

public enum ThemeChoice { System, Light, Dark }

public enum UiScaleChoice { Small, Medium, Large }

/// <summary>The little symbol that marks a category colour.</summary>
public enum MarkerShape { Circle, Square, Bar }

/// <summary>Local, per-installation settings (settings.json in the local data folder).</summary>
public sealed record AppSettings
{
    public string? DataFolder { get; init; }
    public string? DeviceName { get; init; }
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>Global shortcut Ctrl+Alt+B and a tray icon for quick entries (off by default).</summary>
    public bool BackgroundMode { get; init; }

    /// <summary>Folder for the local database and device identity instead of the default one (null = default).</summary>
    public string? LocalDataFolder { get; init; }

    /// <summary>Set after the data folder was changed: the local database is rebuilt from the new folder at the next start.</summary>
    public bool ResetLocalDatabase { get; init; }

    public UiScaleChoice UiScale { get; init; } = UiScaleChoice.Medium;
    public MarkerShape CategoryMarker { get; init; } = MarkerShape.Circle;

    /// <summary>The screen shown at startup: a page key ("dashboard", "transactions"…) or "last" for the one used last.</summary>
    public string StartPage { get; init; } = "dashboard";

    /// <summary>The screen that was open when the program was last used.</summary>
    public string LastPage { get; init; } = "dashboard";

    /// <summary>Show "+" and "−" in front of amounts.</summary>
    public bool ShowMinus { get; init; } = true;

    /// <summary>Expenses in red and income in green.</summary>
    public bool ColorAmounts { get; init; } = true;

    /// <summary>Check GitHub for a newer version at startup.</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>GitHub repository ("owner/name") whose releases are checked for a newer version.</summary>
    public string UpdateRepository { get; init; } = "NepciorLab/BudzetDomowy";

    /// <summary>Access token for reading the releases of a private repository, protected for this Windows user (empty = none).</summary>
    public string? UpdateToken { get; init; }

    /// <summary>Own key for a shortcut (by command id), e.g. "Go.Reports" = "Ctrl+R".</summary>
    public Dictionary<string, string> Shortcuts { get; init; } = [];

    /// <summary>Table columns sized to their content.</summary>
    public bool AutoFitColumns { get; init; }

    /// <summary>Per table (by key): indexes of the columns the user hid.</summary>
    public Dictionary<string, List<int>> HiddenColumns { get; init; } = [];

    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public bool WindowMaximized { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.SettingsDir);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }
}
