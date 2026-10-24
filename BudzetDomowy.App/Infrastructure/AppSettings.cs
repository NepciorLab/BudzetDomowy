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
        Directory.CreateDirectory(AppPaths.LocalDir);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }
}
