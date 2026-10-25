using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Corrections;
using Microsoft.Win32;

namespace BudzetDomowy.Korekta;

/// <summary>Everything that needs a window of the system; tests replace it.</summary>
public interface IUi
{
    string? PickFolder(string? initial);

    string? PickSaveFile(string defaultName);

    bool Confirm(string title, string message);

    void ShowMessage(string title, string message);
}

public sealed class WpfUi : IUi
{
    public string? PickFolder(string? initial)
    {
        var dialog = new OpenFolderDialog { Title = "Wybierz folder z danymi programu (z plikami journal_*.jsonl)", InitialDirectory = initial ?? "" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string defaultName)
    {
        var dialog = new SaveFileDialog { Title = "Zapisz raport kontroli", Filter = "Skoroszyt Excela (*.xlsx)|*.xlsx", FileName = defaultName, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowMessage(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

/// <summary>The data folder opened with the engine of the main program, and the corrections made on it.</summary>
public sealed class Session : IDisposable
{
    public BudgetCore Core { get; }
    public CorrectionRunner Runner { get; }
    public string DataFolder { get; }

    private Session(BudgetCore core, string dataFolder)
    {
        Core = core;
        DataFolder = dataFolder;
        Runner = new CorrectionRunner(core);
    }

    public static string ToolRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BudzetDomowy.Korekta");

    /// <summary>The place of the working data of the tool (its device identity and a database built from the journals), one per data folder.</summary>
    public static string LocalDirFor(string dataFolder, string? root = null)
    {
        var full = Path.GetFullPath(dataFolder).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..10];
        return Path.Combine(root ?? ToolRoot, hash);
    }

    /// <summary>Opens the folder and reads every journal in it (the first time that takes a while).</summary>
    public static Session Open(string dataFolder, string? localRoot = null)
    {
        if (!Directory.Exists(dataFolder)) throw new DirectoryNotFoundException(dataFolder);
        var core = BudgetCore.Create(LocalDirFor(dataFolder, localRoot), dataFolder, "Korekta");
        try
        {
            var status = core.Sync.SyncOnce();
            if (status.UpdateRequired) throw new InvalidOperationException("W folderze są dane nowszej wersji programu. Zaktualizuj narzędzie.");
        }
        catch
        {
            core.Dispose();
            throw;
        }
        return new Session(core, dataFolder);
    }

    public void Dispose() => Core.Dispose();
}

/// <summary>Remembers the last folder and guesses the folder of the main program when the tool lies next to it.</summary>
public static class FolderMemory
{
    private static string SettingsFile => Path.Combine(Session.ToolRoot, "settings.json");

    public static string? Last()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            return doc.RootElement.TryGetProperty("dataFolder", out var p) ? p.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Remember(string folder)
    {
        try
        {
            Directory.CreateDirectory(Session.ToolRoot);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { dataFolder = folder }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* it is only a convenience */ }
    }

    /// <summary>The data folder the main program uses, read from its settings (Data\settings.json next to its .exe, where this tool is expected to lie).</summary>
    public static string? FromMainProgram(string? exeFolder = null)
    {
        try
        {
            var file = Path.Combine(exeFolder ?? AppContext.BaseDirectory, "Data", "settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, "DataFolder", StringComparison.OrdinalIgnoreCase)) return prop.Value.GetString();
            return null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
