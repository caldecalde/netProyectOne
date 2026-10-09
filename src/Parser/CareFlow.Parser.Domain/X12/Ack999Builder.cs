using System.Globalization;
using System.Text;

namespace CareFlow.Parser.Domain.X12;

/// <summary>
/// Builds the 999 implementation acknowledgment (005010X231A1) for a validated interchange: one FA functional group per
/// received group, an AK2 loop per transaction set with IK3/IK4 detail for each error, and IK5/AK9 accept/reject codes.
/// </summary>
/// <remarks>
/// IK404 (copy of the bad data element) is never sent: it would echo PHI back in a document that is stored and
/// forwarded. Positions and error codes are enough for the sender to locate the problem.
/// </remarks>
public static class Ack999Builder
{
    public const string ImplementationGuide = "005010X231A1";

    private const int MaxCodesPerSegment = 5;

    /// <param name="source">The received interchange, after envelope and transaction set validation.</param>
    /// <param name="controlNumber">Interchange control number of the 999 (ISA13 / IEA02), 1 to 999999999.</param>
    /// <param name="now">Creation time written into ISA09/10 and GS04/05.</param>
    /// <returns>The 999 interchange, using the sender's own delimiters, or <c>null</c> when there is no group to acknowledge.</returns>
    public static string? Build(X12Interchange source, int controlNumber, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(controlNumber, 999_999_999);
        if (source.Groups.Count == 0)
        {
            return null;
        }

        var writer = new SegmentWriter(source.Delimiters);
        var isa = source.Isa;
        var interchangeControl = controlNumber.ToString("D9", CultureInfo.InvariantCulture);

        // Sender and receiver swap: the 999 goes back to whoever sent the 837. ISA06/ISA08 keep their fixed width.
        writer.Write(
            "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
            isa[7], isa[8], isa[5], isa[6],
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            isa[11], "00501", interchangeControl, "0", isa[15], source.Delimiters.Component.ToString());

        for (var g = 0; g < source.Groups.Count; g++)
        {
            WriteGroup(writer, source.Groups[g], groupControl: g + 1, now);
        }

        writer.Write("IEA", source.Groups.Count.ToString(CultureInfo.InvariantCulture), interchangeControl);
        return writer.ToString();
    }

    private static void WriteGroup(SegmentWriter writer, X12FunctionalGroup group, int groupControl, DateTimeOffset now)
    {
        var groupNumber = groupControl.ToString(CultureInfo.InvariantCulture);
        writer.Write(
            "GS", "FA", group.Gs[3], group.Gs[2],
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            groupNumber, "X", ImplementationGuide);

        writer.StartTransactionSet();
        const string transactionControl = "0001";
        writer.Write("ST", "999", transactionControl, ImplementationGuide);
        writer.Write("AK1", group.FunctionalIdentifier, group.ControlNumber, group.Version);

        // When the group itself is not supported, its transaction sets were not examined; AK9 alone reports the rejection.
        var groupUnsupported = group.ErrorCodes.Contains(GroupErrorCodes.NotSupported) || group.ErrorCodes.Contains(GroupErrorCodes.VersionNotSupported);
        if (!groupUnsupported)
        {
            foreach (var set in group.TransactionSets)
            {
                WriteTransactionSetResponse(writer, set);
            }
        }

        var received = group.TransactionSets.Count;
        var accepted = groupUnsupported ? 0 : group.TransactionSets.Count(t => t.IsAccepted);
        var status = group.ErrorCodes.Count > 0 || accepted == 0 ? "R" : accepted == received ? "A" : "P";
        var declared = int.TryParse(group.Ge?[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : received;

        writer.Write(
            [
                "AK9", status,
                declared.ToString(CultureInfo.InvariantCulture),
                received.ToString(CultureInfo.InvariantCulture),
                accepted.ToString(CultureInfo.InvariantCulture),
                .. group.ErrorCodes.Distinct().Take(MaxCodesPerSegment),
            ]);

        writer.Write("SE", (writer.TransactionSegmentCount + 1).ToString(CultureInfo.InvariantCulture), transactionControl);
        writer.Write("GE", "1", groupNumber);
    }

    private static void WriteTransactionSetResponse(SegmentWriter writer, X12TransactionSet set)
    {
        writer.Write("AK2", set.Identifier, set.ControlNumber, set.ImplementationReference);
        foreach (var error in set.SegmentErrors)
        {
            writer.Write("IK3", error.SegmentId, error.Position.ToString(CultureInfo.InvariantCulture), error.LoopId ?? string.Empty, error.ErrorCode);
            foreach (var element in error.Elements)
            {
                writer.Write("IK4", element.Position, string.Empty, element.ErrorCode);
            }
        }

        writer.Write(["IK5", set.IsAccepted ? "A" : "R", .. set.ErrorCodes.Distinct().Take(MaxCodesPerSegment)]);
    }

    private sealed class SegmentWriter(X12Delimiters delimiters)
    {
        private readonly StringBuilder _builder = new();
        private int _transactionStart;
        private int _segmentCount;

        public int TransactionSegmentCount => _segmentCount - _transactionStart;

        public void StartTransactionSet() => _transactionStart = _segmentCount;

        public void Write(params string[] elements)
        {
            // Trailing empty elements must be omitted (X12.6); empty elements in the middle keep their separator.
            var length = elements.Length;
            while (length > 1 && elements[length - 1].Length == 0)
            {
                length--;
            }

            for (var i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    _builder.Append(delimiters.Element);
                }

                _builder.Append(elements[i]);
            }

            _builder.Append(delimiters.Segment);
            _segmentCount++;
        }

        public override string ToString() => _builder.ToString();
    }
}
