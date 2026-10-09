namespace CareFlow.Parser.Domain.X12;

// Code lists from the 999 implementation guide (005010X231A1). They are what the 999 reports back to the sender.

/// <summary>IK304: segment syntax error codes.</summary>
public static class SegmentErrorCodes
{
    public const string UnrecognizedSegmentId = "1";
    public const string UnexpectedSegment = "2";
    public const string RequiredSegmentMissing = "3";
    public const string NotInProperSequence = "7";
    public const string SegmentHasDataElementErrors = "8";
}

/// <summary>IK403: data element syntax error codes.</summary>
public static class ElementErrorCodes
{
    public const string RequiredMissing = "1";
    public const string InvalidCharacter = "6";
    public const string InvalidCodeValue = "7";
    public const string InvalidDate = "8";
    public const string TooLong = "5";
}

/// <summary>IK502–IK506: transaction set syntax error codes.</summary>
public static class TransactionSetErrorCodes
{
    public const string NotSupported = "1";
    public const string TrailerMissing = "2";
    public const string ControlNumberMismatch = "3";
    public const string SegmentCountMismatch = "4";
    public const string MissingOrInvalidControlNumber = "7";
    public const string ImplementationSegmentsInError = "I5";
    public const string ImplementationConventionNotSupported = "I6";
}

/// <summary>AK905–AK909: functional group syntax error codes.</summary>
public static class GroupErrorCodes
{
    public const string NotSupported = "1";
    public const string VersionNotSupported = "2";
    public const string TrailerMissing = "3";
    public const string ControlNumberMismatch = "4";
    public const string TransactionSetCountMismatch = "5";
}

/// <param name="Position">IK401: element position, with the component when relevant (<c>5:3</c>).</param>
/// <param name="ErrorCode">IK403, one of <see cref="ElementErrorCodes"/>.</param>
public sealed record X12ElementError(string Position, string ErrorCode);

/// <param name="SegmentId">IK301.</param>
/// <param name="Position">IK302: position in the transaction set, counting ST as 1.</param>
/// <param name="LoopId">IK303, e.g. <c>2300</c>.</param>
/// <param name="ErrorCode">IK304, one of <see cref="SegmentErrorCodes"/>.</param>
public sealed record X12SegmentError(string SegmentId, int Position, string? LoopId, string ErrorCode, IReadOnlyList<X12ElementError> Elements);
