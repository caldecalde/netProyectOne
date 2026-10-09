using System.Text;

namespace CareFlow.Parser.Domain.X12;

public sealed record X12TokenizeResult(X12Delimiters Delimiters, IReadOnlyList<X12Segment> Segments, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
}

/// <summary>
/// Splits an X12 interchange into segments. The delimiters are not fixed by the standard: they are declared by the
/// fixed-width ISA segment (element separator at offset 3, repetition separator ISA11, component separator ISA16,
/// segment terminator right after it), so the ISA is validated byte by byte before anything else is read.
/// Works on the raw bytes with spans; only element values are materialized as strings.
/// </summary>
public static class X12Tokenizer
{
    public const int IsaLength = 106;

    /// <summary>Offsets of the 16 element separators inside the fixed-width ISA segment.</summary>
    private static readonly int[] IsaSeparatorOffsets = [3, 6, 17, 20, 31, 34, 50, 53, 69, 76, 81, 83, 89, 99, 101, 103];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    public static X12TokenizeResult Tokenize(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(Utf8Bom))
        {
            content = content[Utf8Bom.Length..];
        }

        content = content.TrimStart(" \t\r\n"u8);

        if (content.Length < IsaLength || !content.StartsWith("ISA"u8))
        {
            return Fail("x12.isa_missing");
        }

        var element = content[3];
        foreach (var offset in IsaSeparatorOffsets)
        {
            if (content[offset] != element)
            {
                return Fail("x12.isa_malformed");
            }
        }

        var component = content[104];
        var terminator = content[105];
        if (!IsDelimiter(element) || !IsDelimiter(component) || (!IsDelimiter(terminator) && terminator is not ((byte)'\r' or (byte)'\n'))
            || element == component || element == terminator || component == terminator)
        {
            return Fail("x12.isa_invalid_delimiters");
        }

        // ISA11 became the repetition separator in version 00402; before that it was a standards identifier ("U").
        var repetition = content[82];
        var hasRepetition = string.CompareOrdinal(Encoding.ASCII.GetString(content.Slice(84, 5)), "00402") >= 0
            && IsDelimiter(repetition) && repetition != element && repetition != component && repetition != terminator;

        var delimiters = new X12Delimiters((char)element, (char)component, hasRepetition ? (char)repetition : '\0', (char)terminator);
        return new X12TokenizeResult(delimiters, ReadSegments(content, delimiters), ErrorCode: null);
    }

    private static List<X12Segment> ReadSegments(ReadOnlySpan<byte> content, X12Delimiters delimiters)
    {
        var segments = new List<X12Segment>(capacity: Math.Max(16, content.Length / 40));
        var terminator = (byte)delimiters.Segment;
        var element = (byte)delimiters.Element;
        var ordinal = 0;

        while (!content.IsEmpty)
        {
            // Line breaks between segments are common ("~\r\n") and are not part of the data, unless they are the terminator.
            content = SkipLineBreaks(content, terminator);
            if (content.IsEmpty)
            {
                break;
            }

            var end = content.IndexOf(terminator);
            var raw = end < 0 ? content : content[..end];
            if (terminator == (byte)'\n')
            {
                raw = raw.TrimEnd((byte)'\r');
            }

            content = end < 0 ? [] : content[(end + 1)..];

            if (raw.Trim(" \t\r\n"u8).IsEmpty)
            {
                continue;
            }

            segments.Add(new X12Segment(SplitElements(raw, element), ++ordinal, delimiters));
        }

        return segments;
    }

    private static string[] SplitElements(ReadOnlySpan<byte> segment, byte separator)
    {
        var count = segment.Count(separator) + 1;
        var elements = new string[count];
        for (var i = 0; i < count; i++)
        {
            var next = segment.IndexOf(separator);
            var value = next < 0 ? segment : segment[..next];
            elements[i] = value.IsEmpty ? string.Empty : Encoding.UTF8.GetString(value);
            segment = next < 0 ? [] : segment[(next + 1)..];
        }

        return elements;
    }

    private static ReadOnlySpan<byte> SkipLineBreaks(ReadOnlySpan<byte> content, byte terminator)
    {
        var i = 0;
        while (i < content.Length && content[i] is (byte)'\r' or (byte)'\n' && content[i] != terminator)
        {
            i++;
        }

        return content[i..];
    }

    private static bool IsDelimiter(byte b) => b is > 0x20 and < 0x7F && !char.IsLetterOrDigit((char)b);

    private static X12TokenizeResult Fail(string code) => new(default, [], code);
}
