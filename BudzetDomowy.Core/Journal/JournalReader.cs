using System.Text.Json;

namespace BudzetDomowy.Core.Journal;

public sealed record JournalReadResult(
    IReadOnlyList<JournalEvent> Events,
    /// <summary>Offset of the first byte not yet consumed (always at a line start).</summary>
    long NewOffset,
    /// <summary>Terminated lines that were not valid events; skipped.</summary>
    int CorruptLines,
    /// <summary>Set when a line with a newer schema was met; reading stopped before it.</summary>
    int? UnsupportedSchemaVersion,
    /// <summary>True when the file was shorter than the stored offset and was read from the start.</summary>
    bool FileShrank);

public static class JournalReader
{
    /// <summary>
    /// Reads complete lines from <paramref name="offset"/>. An unterminated last line (still being written or
    /// synchronised) is left for the next call. Opens with FileShare.ReadWrite and retries on IO errors.
    /// </summary>
    public static JournalReadResult ReadFrom(string path, long offset, int maxSupportedSchema = JournalEvent.CurrentSchemaVersion)
    {
        var shrank = false;
        var data = FileRetry.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length)
            {
                shrank = true;
                offset = 0;
            }
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - offset];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer[..read];
        });

        var events = new List<JournalEvent>();
        var corrupt = 0;
        int? unsupported = null;
        var consumed = 0;
        var pos = 0;

        while (pos < data.Length)
        {
            var nl = Array.IndexOf(data, (byte)'\n', pos);
            if (nl < 0) break; // incomplete last line

            var line = data.AsSpan(pos, nl - pos);
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (offset == 0 && pos == 0 && line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
                line = line[3..];

            if (!IsBlank(line))
            {
                var version = PeekSchemaVersion(line);
                if (version > maxSupportedSchema)
                {
                    unsupported = version;
                    break; // stop before this line; do not consume it
                }

                var ev = TryParse(line);
                if (ev is null) corrupt++;
                else events.Add(ev);
            }

            pos = nl + 1;
            consumed = pos;
        }

        return new JournalReadResult(events, offset + consumed, corrupt, unsupported, shrank);
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
            if (b is not (0x20 or 0x09 or 0x0D or 0x0A)) return false;
        return true;
    }

    private static int PeekSchemaVersion(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return 0;
            var depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0 && reader.TokenType == JsonTokenType.PropertyName
                         && reader.ValueTextEquals("schemaVersion"u8))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : 0;
                }
            }
        }
        catch (JsonException) { }
        return 0;
    }

    private static JournalEvent? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            var ev = JournalJson.Deserialize(line);
            if (ev is null || ev.EventId == Guid.Empty || ev.EntityId == Guid.Empty
                || ev.DeviceId.Length == 0 || ev.EntityType.Length == 0 || ev.SchemaVersion < 1)
                return null;
            return ev;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
