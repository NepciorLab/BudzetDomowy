using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudzetDomowy.App.Infrastructure;

public enum ThemeChoice { System, Light, Dark }

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
