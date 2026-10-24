using System.Text.Json;

namespace BudzetDomowy.Core.Sync;

/// <summary>Identity of this installation, kept in the local data folder (never in the synchronised folder).</summary>
public sealed record DeviceIdentity(string DeviceId, string DeviceName)
{
    private const string FileName = "device.json";

    /// <summary>Loads device.json from <paramref name="localDataDir"/> or creates it with a new GUID.</summary>
    public static DeviceIdentity LoadOrCreate(string localDataDir, string? deviceName = null)
    {
        Directory.CreateDirectory(localDataDir);
        var path = Path.Combine(localDataDir, FileName);
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path));
                if (existing is not null && Guid.TryParse(existing.DeviceId, out var g))
                    return existing with { DeviceId = g.ToString("D") };
            }
            catch (JsonException) { /* fall through and recreate */ }
        }

        var created = new DeviceIdentity(Guid.NewGuid().ToString("D"), deviceName ?? Environment.MachineName);
        File.WriteAllText(path, JsonSerializer.Serialize(created));
        return created;
    }
}
