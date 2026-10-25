using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BudzetDomowy.Core.Journal;

public enum EventOperation { Upsert, Delete }

/// <summary>One line of a device journal. The payload of an Upsert is the complete entity (never a patch).</summary>
public sealed record JournalEvent
{
    /// <summary>The newest event schema this program reads. Kinds that exist since schema 2 (payees, planned payments) are written with it.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>What every event of the older kinds is written with, so older programs keep reading them.</summary>
    public const int BaseSchemaVersion = 1;

    public Guid EventId { get; init; }
    public string DeviceId { get; init; } = "";
    public HlcTimestamp Timestamp { get; init; }
    public string EntityType { get; init; } = "";
    public Guid EntityId { get; init; }
    public EventOperation Operation { get; init; }
    /// <summary>Entity JSON for Upsert; null for Delete.</summary>
    public JsonNode? Payload { get; init; }
    public int SchemaVersion { get; init; } = BaseSchemaVersion;
}

public static class JournalJson
{
    /// <summary>Options for the event envelope and entity payloads.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new DateOnlyJsonConverter());
        return o;
    }

    public static string Serialize(JournalEvent e) => JsonSerializer.Serialize(e, Options);

    public static JournalEvent? Deserialize(ReadOnlySpan<byte> utf8Line) =>
        JsonSerializer.Deserialize<JournalEvent>(utf8Line, Options);

    public static JsonNode ToPayload<T>(T entity) => JsonSerializer.SerializeToNode(entity, Options)!;

    public static T? FromPayload<T>(JsonNode? payload) => payload is null ? default : payload.Deserialize<T>(Options);
}

internal sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateOnly.ParseExact(reader.GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
}
