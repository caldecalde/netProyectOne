using System.Globalization;
using NHapi.Base.Model;

namespace CareFlow.Parser.Infrastructure.Hl7;

/// <summary>Conversions from HL7 v2 primitive formats.</summary>
internal static class Hl7Values
{
    /// <summary>
    /// Parses an HL7 DTM/TS value: <c>YYYY[MM[DD[HH[MM[SS[.S...]]]]]][+/-ZZZZ]</c>. Missing parts default to their
    /// minimum; a value without an offset is taken as UTC, since HL7 leaves the zone to site agreement.
    /// </summary>
    public static DateTimeOffset? Timestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        var offset = TimeSpan.Zero;
        var signAt = value.IndexOfAny(['+', '-'], 4);
        if (signAt > 0)
        {
            var zone = value[(signAt + 1)..];
            if (zone.Length != 4 || !int.TryParse(zone, NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
            {
                return null;
            }

            offset = new TimeSpan(hhmm / 100, hhmm % 100, 0);
            if (value[signAt] == '-')
            {
                offset = -offset;
            }

            value = value[..signAt];
        }

        var fraction = 0d;
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!double.TryParse("0" + value[dot..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fraction))
            {
                return null;
            }

            value = value[..dot];
        }

        if (value.Length is not (4 or 6 or 8 or 10 or 12 or 14) || !value.All(char.IsAsciiDigit))
        {
            return null;
        }

        int Part(int start, int fallback) => value.Length >= start + 2 ? int.Parse(value.AsSpan(start, 2), CultureInfo.InvariantCulture) : fallback;

        try
        {
            var dateTime = new DateTime(
                int.Parse(value.AsSpan(0, 4), CultureInfo.InvariantCulture), Part(4, 1), Part(6, 1), Part(8, 0), Part(10, 0), Part(12, 0), DateTimeKind.Unspecified);
            return new DateTimeOffset(dateTime.AddSeconds(fraction), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Parses the date part (<c>YYYYMMDD</c>) of a DT/DTM value, e.g. a date of birth.</summary>
    public static DateOnly? Date(string? value) =>
        value is { Length: >= 8 } && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Renders an OBX-5 value: primitives as-is, composites (CE, CWE, ...) with <c>^</c> between components.</summary>
    public static string? Render(IType? data) => data switch
    {
        IPrimitive primitive => Text(primitive.Value),
        IComposite composite => Text(string.Join('^', composite.Components.Select(c => Render(c) ?? string.Empty)).TrimEnd('^')),
        _ => null,
    };
}
