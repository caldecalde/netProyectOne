namespace CareFlow.Parser.Domain;

public enum IssueSeverity
{
    Warning = 0,
    Error = 1,
}

/// <summary>
/// A problem found while parsing. <see cref="Location"/> and <see cref="Description"/> are built from segment and
/// field positions only, never from field values, so issues can be stored, shown and logged without exposing PHI.
/// </summary>
public sealed record ParseIssue(IssueSeverity Severity, string Code, string Location, string Description)
{
    /// <summary>The HL7 message or X12 transaction set the issue belongs to, when known.</summary>
    public string? ControlId { get; init; }
}

/// <summary>Everything a parser produced for one file.</summary>
public sealed record ParseOutcome
{
    public required IReadOnlyList<ParsedMessage> Messages { get; init; }

    /// <summary>HL7 messages or X12 transaction sets that were found but rejected.</summary>
    public required int RejectedCount { get; init; }

    public required IReadOnlyList<ParseIssue> Issues { get; init; }

    /// <summary>Set when nothing in the file is usable, e.g. <c>x12.interchange_invalid</c>.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Acknowledgment to return to the sender (an X12 999), when the format defines one.</summary>
    public string? Acknowledgment { get; init; }

    public bool IsSuccess => FailureCode is null && Messages.Count > 0;

    public static ParseOutcome Failure(string failureCode, params ParseIssue[] issues) => new()
    {
        Messages = [],
        RejectedCount = 0,
        Issues = issues,
        FailureCode = failureCode,
    };
}
