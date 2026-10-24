using System.Security.Cryptography;
using System.Text;

namespace BudzetDomowy.Core.Domain;

/// <summary>Stable ids derived from other values, so two devices generating the same thing create the same entity.</summary>
public static class DeterministicId
{
    /// <summary>Id of a transaction generated from a recurring template for a given date.</summary>
    public static Guid ForRecurrence(Guid templateId, DateOnly date) =>
        FromString($"recurrence:{templateId:D}:{date:yyyy-MM-dd}");

    public static Guid FromString(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5-like
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}

public static class Money
{
    public static decimal RoundPln(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal ToPln(decimal amount, decimal rateToPln) => RoundPln(amount * rateToPln);

    public static long ToCents(decimal value) => (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromCents(long cents) => cents / 100m;
}
