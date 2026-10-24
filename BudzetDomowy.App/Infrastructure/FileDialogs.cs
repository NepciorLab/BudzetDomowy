using System.Text.Json;
using BudzetDomowy.Core.Excel;
using Microsoft.Win32;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>File pickers behind an interface so view models never open a window themselves (and tests can fake them).</summary>
public interface IFileDialogs
{
    /// <param name="filter">Windows filter text, e.g. "Excel|*.xlsx;*.xlsm".</param>
    string? OpenFile(string title, string filter);

    string? SaveFile(string title, string filter, string defaultFileName);
}

public sealed class WpfFileDialogs : IFileDialogs
{
    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

/// <summary>Remembers, per computer, which program category a category name from an imported file was mapped to.</summary>
public sealed class ImportMappingStore
{
    private sealed record Entry(Guid? ExistingId, string? NewName);

    private readonly string _path;

    public ImportMappingStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.LocalDir, "import-mapping.json");

    public IReadOnlyDictionary<string, CategoryChoice> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, CategoryChoice>();
            var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? [];
            return raw.ToDictionary(kv => kv.Key, kv => new CategoryChoice(kv.Value.ExistingId, kv.Value.NewName));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, CategoryChoice>();
        }
    }

    public void Save(IReadOnlyDictionary<string, CategoryChoice> mapping)
    {
        var existing = Load().ToDictionary(kv => kv.Key, kv => kv.Value);
        // remember only real decisions; a "create new" choice becomes an existing category after the import
        foreach (var (key, choice) in mapping) existing[key] = choice;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var raw = existing.ToDictionary(kv => kv.Key, kv => new Entry(kv.Value.ExistingId, kv.Value.NewName));
        File.WriteAllText(_path, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
    }
}
