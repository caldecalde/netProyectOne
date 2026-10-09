namespace CareFlow.Ingestion.Domain;

public enum FileFormat
{
    Unknown = 0,

    /// <summary>HL7 v2.x pipe-delimited message(s), starting with an MSH segment.</summary>
    Hl7v2 = 1,

    /// <summary>ASC X12 interchange (837, 835, ...), starting with a fixed-width ISA segment.</summary>
    X12 = 2,
}

public static class FileFormatDetector
{
    /// <summary>Bytes of the file header needed for detection.</summary>
    public const int HeaderLength = 128;

    private const byte MllpStartBlock = 0x0B;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Hl7Header => "MSH"u8;

    private static ReadOnlySpan<byte> X12Header => "ISA"u8;

    /// <summary>
    /// Detects the format from the first bytes of the file. Detection is content based;
    /// the file name or extension supplied by the client is never trusted.
    /// </summary>
    public static FileFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Utf8Bom))
        {
            header = header[Utf8Bom.Length..];
        }

        // Tolerate an MLLP frame start and leading whitespace / blank lines.
        while (!header.IsEmpty && (header[0] == MllpStartBlock || char.IsWhiteSpace((char)header[0])))
        {
            header = header[1..];
        }

        // MSH-1 (field separator) is the 4th character; the delimiters follow it, e.g. "MSH|^~\&|".
        if (header.Length >= 8 && header.StartsWith(Hl7Header) && IsDelimiter(header[3]))
        {
            return FileFormat.Hl7v2;
        }

        // ISA is fixed width: the element separator is the 4th character and ISA is 106 characters long.
        if (header.Length >= 106 && header.StartsWith(X12Header) && IsDelimiter(header[3]) && header[103] == header[3])
        {
            return FileFormat.X12;
        }

        return FileFormat.Unknown;
    }

    private static bool IsDelimiter(byte b) => b is > 0x20 and < 0x7F && !char.IsLetterOrDigit((char)b);
}
