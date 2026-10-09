using System.Globalization;

namespace CareFlow.Parser.Domain.X12;

/// <summary>
/// Builds the ISA/GS/ST envelope structure and checks its control totals: trailer present, control numbers matching
/// between header and trailer, and segment / transaction set / group counts matching what was actually received.
/// It knows nothing about any specific transaction set.
/// </summary>
public static class X12EnvelopeReader
{
    public static X12Interchange Read(X12TokenizeResult tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!tokens.IsSuccess || tokens.Segments.Count == 0)
        {
            throw new ArgumentException("Tokenization failed; there is no envelope to read.", nameof(tokens));
        }

        var segments = tokens.Segments;
        var interchange = new X12Interchange(tokens.Delimiters, segments[0]);
        ValidateIsa(interchange);

        var i = 1;
        while (i < segments.Count)
        {
            var segment = segments[i];
            switch (segment.Id)
            {
                case "GS":
                    interchange.Groups.Add(ReadGroup(segments, ref i, interchange.Errors));
                    break;
                case "IEA":
                    interchange.Iea = segment;
                    i++;
                    if (i < segments.Count)
                    {
                        interchange.Errors.Add(Error("x12.data_after_iea", segments[i], "Data follows the IEA trailer; only one interchange per file is supported."));
                    }

                    i = segments.Count;
                    break;
                default:
                    interchange.Errors.Add(Error("x12.unexpected_segment", segment, "Segment is not allowed between functional groups."));
                    i++;
                    break;
            }
        }

        ValidateIea(interchange);
        return interchange;
    }

    private static X12FunctionalGroup ReadGroup(IReadOnlyList<X12Segment> segments, ref int i, List<ParseIssue> errors)
    {
        var group = new X12FunctionalGroup(segments[i]);
        i++;

        while (i < segments.Count && segments[i].Id is not ("GE" or "GS" or "IEA"))
        {
            if (segments[i].Id == "ST")
            {
                group.TransactionSets.Add(ReadTransactionSet(segments, ref i));
            }
            else
            {
                errors.Add(Error("x12.segment_outside_transaction", segments[i], "Segment is not inside an ST/SE transaction set."));
                i++;
            }
        }

        if (i < segments.Count && segments[i].Id == "GE")
        {
            group.Ge = segments[i];
            i++;
            if (group.Ge[2] != group.ControlNumber)
            {
                group.ErrorCodes.Add(GroupErrorCodes.ControlNumberMismatch);
            }

            if (!int.TryParse(group.Ge[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != group.TransactionSets.Count)
            {
                group.ErrorCodes.Add(GroupErrorCodes.TransactionSetCountMismatch);
            }
        }
        else
        {
            group.ErrorCodes.Add(GroupErrorCodes.TrailerMissing);
        }

        return group;
    }

    private static X12TransactionSet ReadTransactionSet(IReadOnlyList<X12Segment> segments, ref int i)
    {
        var start = i;
        X12Segment? se = null;
        i++;
        while (i < segments.Count && segments[i].Id is not ("ST" or "GE" or "GS" or "IEA"))
        {
            if (segments[i].Id == "SE")
            {
                se = segments[i];
                i++;
                break;
            }

            i++;
        }

        var body = new X12Segment[i - start];
        for (var k = 0; k < body.Length; k++)
        {
            body[k] = segments[start + k];
        }

        var set = new X12TransactionSet(segments[start], body, se);
        if (set.ControlNumber.Length is < 4 or > 9)
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.MissingOrInvalidControlNumber);
        }

        if (se is null)
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.TrailerMissing);
            return set;
        }

        if (se[2] != set.ControlNumber)
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.ControlNumberMismatch);
        }

        // SE01 counts every segment from ST to SE inclusive.
        if (!int.TryParse(se[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != body.Length)
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.SegmentCountMismatch);
        }

        return set;
    }

    private static void ValidateIsa(X12Interchange interchange)
    {
        var isa = interchange.Isa;
        if (isa.ElementCount != 16)
        {
            interchange.Errors.Add(Error("x12.isa_malformed", isa, "ISA must have exactly 16 elements."));
            return;
        }

        if (isa[12] != "00501")
        {
            interchange.Errors.Add(Error("x12.unsupported_version", isa, "Only interchange control version 00501 is supported (ISA12)."));
        }

        if (isa[13].Length != 9 || !isa[13].All(char.IsAsciiDigit))
        {
            interchange.Errors.Add(Error("x12.invalid_control_number", isa, "ISA13 must be 9 digits."));
        }

        if (isa[15] is not ("P" or "T"))
        {
            interchange.Errors.Add(Error("x12.invalid_usage_indicator", isa, "ISA15 must be P or T."));
        }
    }

    private static void ValidateIea(X12Interchange interchange)
    {
        if (interchange.Iea is not { } iea)
        {
            interchange.Errors.Add(new ParseIssue(IssueSeverity.Error, "x12.iea_missing", "IEA", "The interchange has no IEA trailer; the file may be truncated."));
            return;
        }

        if (iea[2] != interchange.ControlNumber)
        {
            interchange.Errors.Add(Error("x12.interchange_control_mismatch", iea, "IEA02 does not match ISA13."));
        }

        if (!int.TryParse(iea[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != interchange.Groups.Count)
        {
            interchange.Errors.Add(Error("x12.group_count_mismatch", iea, "IEA01 does not match the number of functional groups."));
        }
    }

    private static ParseIssue Error(string code, X12Segment segment, string description) =>
        new(IssueSeverity.Error, code, $"{segment.Id} (segment {segment.Ordinal})", description);
}
