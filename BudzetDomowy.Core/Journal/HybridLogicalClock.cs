using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudzetDomowy.Core.Journal;

/// <summary>Hybrid logical timestamp: UTC wall time in milliseconds plus a logical counter. Totally ordered.</summary>
[JsonConverter(typeof(HlcTimestampJsonConverter))]
public readonly record struct HlcTimestamp(long WallMs, int Counter) : IComparable<HlcTimestamp>
{
    public static readonly HlcTimestamp Zero = new(0, 0);

    public int CompareTo(HlcTimestamp other)
    {
        var c = WallMs.CompareTo(other.WallMs);
        return c != 0 ? c : Counter.CompareTo(other.Counter);
    }

    public static bool operator <(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) < 0;
    public static bool operator >(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) > 0;
    public static bool operator <=(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) <= 0;
    public static bool operator >=(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) >= 0;

    public DateTimeOffset WallTime => DateTimeOffset.FromUnixTimeMilliseconds(WallMs);

    /// <summary>Sortable text form, e.g. 001767225600000.00003.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{WallMs:D15}.{Counter:D5}");

    public static bool TryParse(string? text, out HlcTimestamp value)
    {
        value = Zero;
        if (string.IsNullOrEmpty(text)) return false;
        var dot = text.IndexOf('.');
        if (dot <= 0) return false;
        if (!long.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var wall)) return false;
        if (!int.TryParse(text.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var counter)) return false;
        value = new HlcTimestamp(wall, counter);
        return true;
    }

    public static HlcTimestamp Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid HLC timestamp: '{text}'.");
}

public sealed class HlcTimestampJsonConverter : JsonConverter<HlcTimestamp>
{
    public override HlcTimestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        HlcTimestamp.Parse(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, HlcTimestamp value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Thread-safe hybrid logical clock (Kulkarni et al.). Never returns the same or a smaller value twice.</summary>
public sealed class HybridLogicalClock
{
    private readonly Func<long> _wallClockMs;
    private readonly object _gate = new();
    private HlcTimestamp _last;

    public HybridLogicalClock(Func<long>? wallClockMs = null, HlcTimestamp? initial = null)
    {
        _wallClockMs = wallClockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _last = initial ?? HlcTimestamp.Zero;
    }

    public HlcTimestamp Last
    {
        get { lock (_gate) return _last; }
    }

    /// <summary>Timestamp for a local event.</summary>
    public HlcTimestamp Now()
    {
        lock (_gate)
        {
            var wall = _wallClockMs();
            _last = wall > _last.WallMs
                ? new HlcTimestamp(wall, 0)
                : new HlcTimestamp(_last.WallMs, _last.Counter + 1);
            return _last;
        }
    }

    /// <summary>Advances the clock after seeing a remote timestamp, so later local events sort after it.</summary>
    public void Observe(HlcTimestamp remote)
    {
        lock (_gate)
        {
            if (remote > _last) _last = remote;
        }
    }
}
