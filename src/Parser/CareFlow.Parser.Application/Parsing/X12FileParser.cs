using System.Globalization;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Domain;
using CareFlow.Parser.Domain.X12;

namespace CareFlow.Parser.Application.Parsing;

/// <summary>
/// X12 pipeline: tokenize, validate the envelope, parse each 837P transaction set, and build the 999 that accepts or
/// rejects each one. Only 837P (005010X222A1) in HC functional groups is supported.
/// </summary>
public sealed class X12FileParser : IFileParser
{
    private const string Supported837PGuide = "005010X222";

    public string Format => "X12";

    public ParseOutcome Parse(ReadOnlyMemory<byte> content, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tokens = X12Tokenizer.Tokenize(content.Span);
        if (!tokens.IsSuccess)
        {
            return ParseOutcome.Failure(tokens.ErrorCode!, new ParseIssue(IssueSeverity.Error, tokens.ErrorCode!, "ISA", "The ISA interchange header is missing or malformed."));
        }

        var interchange = X12EnvelopeReader.Read(tokens);
        if (interchange.Errors.Count > 0)
        {
            // TA1 territory: the envelope itself is untrustworthy, so no 999 is produced.
            return ParseOutcome.Failure("x12.interchange_invalid", [.. interchange.Errors]);
        }

        var messages = new List<ParsedMessage>();
        var issues = new List<ParseIssue>();
        var received = 0;

        foreach (var group in interchange.Groups)
        {
            received += group.TransactionSets.Count;
            if (group.FunctionalIdentifier != "HC")
            {
                group.ErrorCodes.Add(GroupErrorCodes.NotSupported);
            }
            else if (!group.Version.StartsWith(Supported837PGuide, StringComparison.Ordinal))
            {
                group.ErrorCodes.Add(GroupErrorCodes.VersionNotSupported);
            }

            var groupMessages = new List<ParsedMessage>();
            if (!group.ErrorCodes.Contains(GroupErrorCodes.NotSupported) && !group.ErrorCodes.Contains(GroupErrorCodes.VersionNotSupported))
            {
                foreach (var set in group.TransactionSets)
                {
                    if (ParseTransactionSet(set, issues) is { } message)
                    {
                        groupMessages.Add(message);
                    }
                }
            }

            // A rejected group rejects every transaction set in it, even ones that were individually valid.
            if (group.ErrorCodes.Count == 0)
            {
                messages.AddRange(groupMessages);
            }
            else
            {
                issues.AddRange(group.ErrorCodes.Distinct().Select(code => new ParseIssue(
                    IssueSeverity.Error, "x12.group_rejected", $"GS {group.ControlNumber}", Describe.Group(code))));
            }
        }

        return new ParseOutcome
        {
            Messages = messages,
            RejectedCount = received - messages.Count,
            Issues = issues,
            Acknowledgment = Ack999Builder.Build(interchange, AckControlNumber(context.IngestionId), context.Now),
            FailureCode = messages.Count == 0 ? "x12.no_accepted_transactions" : null,
        };
    }

    private static ParsedMessage? ParseTransactionSet(X12TransactionSet set, List<ParseIssue> issues)
    {
        ParsedMessage? message = null;
        if (set.Identifier != "837")
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.NotSupported);
        }
        else if (!set.ImplementationReference.StartsWith(Supported837PGuide, StringComparison.Ordinal))
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.ImplementationConventionNotSupported);
        }
        else
        {
            // Parsed even when the envelope found errors, so the 999 can report every problem in one round trip.
            (message, var warnings) = Claim837PParser.Parse(set);
            issues.AddRange(warnings);
        }

        var location = $"ST {set.ControlNumber}";
        issues.AddRange(set.ErrorCodes.Distinct()
            .Where(code => code != TransactionSetErrorCodes.ImplementationSegmentsInError)
            .Select(code => new ParseIssue(IssueSeverity.Error, "x12.transaction_set_rejected", location, Describe.TransactionSet(code)) { ControlId = set.ControlNumber }));
        issues.AddRange(set.SegmentErrors.Select(error => new ParseIssue(
            IssueSeverity.Error,
            "x12.segment_error",
            $"{location} / {error.SegmentId} (position {error.Position}{(error.LoopId is null ? string.Empty : $", loop {error.LoopId}")})",
            Describe.Segment(error)) { ControlId = set.ControlNumber }));
        return message;
    }

    /// <summary>A stable 9-digit control number per ingestion, so reprocessing a file reproduces the same 999.</summary>
    internal static int AckControlNumber(Guid ingestionId)
    {
        Span<byte> bytes = stackalloc byte[16];
        ingestionId.TryWriteBytes(bytes);
        return (int)(BitConverter.ToUInt32(bytes) % 999_999_999) + 1;
    }

    private static class Describe
    {
        public static string Group(string code) => code switch
        {
            GroupErrorCodes.NotSupported => "Functional group is not supported; only HC (health care claim) groups are accepted.",
            GroupErrorCodes.VersionNotSupported => "Functional group version is not supported; only 005010X222A1 (837P) is accepted.",
            GroupErrorCodes.TrailerMissing => "GE trailer is missing.",
            GroupErrorCodes.ControlNumberMismatch => "GE02 does not match GS06.",
            GroupErrorCodes.TransactionSetCountMismatch => "GE01 does not match the number of transaction sets received.",
            _ => $"Functional group error {code}.",
        };

        public static string TransactionSet(string code) => code switch
        {
            TransactionSetErrorCodes.NotSupported => "Transaction set is not supported; only 837 is accepted.",
            TransactionSetErrorCodes.TrailerMissing => "SE trailer is missing.",
            TransactionSetErrorCodes.ControlNumberMismatch => "SE02 does not match ST02.",
            TransactionSetErrorCodes.SegmentCountMismatch => "SE01 does not match the number of segments received.",
            TransactionSetErrorCodes.MissingOrInvalidControlNumber => "ST02 is missing or invalid.",
            TransactionSetErrorCodes.ImplementationConventionNotSupported => "ST03 implementation guide is not supported; 005010X222A1 is expected.",
            _ => $"Transaction set error {code}.",
        };

        public static string Segment(X12SegmentError error)
        {
            var text = error.ErrorCode switch
            {
                SegmentErrorCodes.UnrecognizedSegmentId => "Unrecognized segment ID.",
                SegmentErrorCodes.UnexpectedSegment => "Segment is not expected here.",
                SegmentErrorCodes.RequiredSegmentMissing => "Required segment is missing.",
                SegmentErrorCodes.NotInProperSequence => "Segment is not in the proper sequence.",
                SegmentErrorCodes.SegmentHasDataElementErrors => "Segment has data element errors:",
                _ => $"Segment error {error.ErrorCode}.",
            };
            if (error.Elements.Count == 0)
            {
                return text;
            }

            return text + " " + string.Join("; ", error.Elements.Select(e => $"{Reference(error.SegmentId, e.Position)} {Element(e.ErrorCode)}"));
        }

        /// <summary><c>CLM</c> + <c>5:3</c> becomes <c>CLM05-3</c>.</summary>
        private static string Reference(string segmentId, string position)
        {
            var parts = position.Split(':');
            var element = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString("D2", CultureInfo.InvariantCulture) : parts[0];
            return parts.Length > 1 ? $"{segmentId}{element}-{parts[1]}" : $"{segmentId}{element}";
        }

        private static string Element(string code) => code switch
        {
            ElementErrorCodes.RequiredMissing => "is required but missing",
            ElementErrorCodes.InvalidCharacter => "has an invalid character or number format",
            ElementErrorCodes.InvalidCodeValue => "has an invalid code value",
            ElementErrorCodes.InvalidDate => "has an invalid date",
            ElementErrorCodes.TooLong => "is too long",
            _ => $"has error {code}",
        };
    }
}
