namespace CareFlow.Parser.Domain.X12;

/// <summary>ISA ... IEA: one interchange, holding functional groups.</summary>
public sealed class X12Interchange
{
    internal X12Interchange(X12Delimiters delimiters, X12Segment isa)
    {
        Delimiters = delimiters;
        Isa = isa;
    }

    public X12Delimiters Delimiters { get; }

    public X12Segment Isa { get; }

    public X12Segment? Iea { get; internal set; }

    public string ControlNumber => Isa[13];

    /// <summary>ISA15: <c>P</c> production or <c>T</c> test.</summary>
    public string UsageIndicator => Isa[15];

    public List<X12FunctionalGroup> Groups { get; } = [];

    /// <summary>
    /// Envelope errors at interchange level. A real trading partner answers these with a TA1;
    /// CareFlow rejects the file without a 999, because the groups inside cannot be trusted.
    /// </summary>
    public List<ParseIssue> Errors { get; } = [];
}

/// <summary>GS ... GE: transaction sets of one functional type (<c>HC</c> = health care claim).</summary>
public sealed class X12FunctionalGroup(X12Segment gs)
{
    public X12Segment Gs { get; } = gs;

    public X12Segment? Ge { get; internal set; }

    public string FunctionalIdentifier => Gs[1];

    public string ControlNumber => Gs[6];

    /// <summary>GS08, e.g. <c>005010X222A1</c>.</summary>
    public string Version => Gs[8];

    public List<X12TransactionSet> TransactionSets { get; } = [];

    /// <summary>AK905–AK909 codes. Any code rejects the whole group.</summary>
    public List<string> ErrorCodes { get; } = [];
}

/// <summary>ST ... SE: one business document, e.g. one 837P.</summary>
public sealed class X12TransactionSet(X12Segment st, IReadOnlyList<X12Segment> segments, X12Segment? se)
{
    public X12Segment St { get; } = st;

    /// <summary>All segments from ST through SE (or up to where SE should have been).</summary>
    public IReadOnlyList<X12Segment> Segments { get; } = segments;

    public X12Segment? Se { get; } = se;

    /// <summary>ST01, e.g. <c>837</c>.</summary>
    public string Identifier => St[1];

    public string ControlNumber => St[2];

    /// <summary>ST03, e.g. <c>005010X222A1</c>.</summary>
    public string ImplementationReference => St[3];

    /// <summary>IK502–IK506 codes.</summary>
    public List<string> ErrorCodes { get; } = [];

    public List<X12SegmentError> SegmentErrors { get; } = [];

    public bool IsAccepted => ErrorCodes.Count == 0 && SegmentErrors.Count == 0;

    /// <summary>Position of <paramref name="segment"/> inside this transaction set, counting ST as 1 (IK302).</summary>
    public int PositionOf(X12Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Ordinal - St.Ordinal + 1;
    }

    /// <summary>The segments between ST and SE.</summary>
    public IEnumerable<X12Segment> Body => Segments.Skip(1).Take(Segments.Count - (Se is null ? 1 : 2));
}
