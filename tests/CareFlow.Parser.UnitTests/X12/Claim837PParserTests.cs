using CareFlow.Parser.Domain;
using CareFlow.Parser.Domain.X12;

namespace CareFlow.Parser.UnitTests.X12;

public class Claim837PParserTests
{
    [Fact]
    public void Parse_SingleClaimSample_MapsEveryLoop()
    {
        var set = X12TestData.Read(X12TestData.SingleClaim).Groups[0].TransactionSets[0];

        var (message, warnings) = Claim837PParser.Parse(set);

        Assert.True(set.IsAccepted, string.Join(", ", set.SegmentErrors));
        Assert.Empty(warnings);
        Assert.NotNull(message);
        Assert.Equal("837P", message.MessageType);
        Assert.Equal("0001", message.ControlId);
        Assert.Equal("CAREFLOW CLINIC", message.Sender);
        Assert.Equal("CAREFLOW PAYER", message.Receiver);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero), message.CreatedAt);

        var claim = Assert.Single(message.Claims);
        Assert.Equal("CLM-000123", claim.ClaimId);
        Assert.Equal(150m, claim.TotalCharge);
        Assert.Equal("11", claim.PlaceOfService);
        Assert.Equal("1", claim.FrequencyCode);
        Assert.Equal(new ProviderRecord("CAREFLOW CLINIC", "1234567893", "123456789", new PostalAddress("100 CENTRAL AVE", null, "ORLANDO", "FL", "32801", null)), claim.BillingProvider);
        Assert.Equal("MORA, LUIS", claim.RenderingProvider?.Name);
        Assert.Equal("MBR000123", claim.Subscriber?.MemberId);
        Assert.Equal(new PersonName("RIVERA", "ANA", null), claim.Subscriber?.Name);
        Assert.Equal(new DateOnly(1985, 2, 14), claim.Subscriber?.BirthDate);
        Assert.Equal("F", claim.Subscriber?.Sex);
        Assert.Equal("18", claim.Subscriber?.RelationshipCode);
        Assert.Null(claim.Patient);
        Assert.Equal(new OrganizationRef("CAREFLOW PAYER", "CFPAYER01"), claim.Payer);
        Assert.Equal("GRP001", claim.GroupNumber);
        Assert.Equal("P", claim.PayerResponsibility);
        Assert.Equal("CI", claim.ClaimFilingIndicator);
        Assert.Equal([new DiagnosisCode("ABK", "J069")], claim.Diagnoses);

        var line = Assert.Single(claim.ServiceLines);
        Assert.Equal(1, line.LineNumber);
        Assert.Equal("HC", line.ProcedureQualifier);
        Assert.Equal("99213", line.ProcedureCode);
        Assert.Equal(150m, line.Charge);
        Assert.Equal("UN", line.UnitType);
        Assert.Equal(1m, line.Units);
        Assert.Equal([1], line.DiagnosisPointers);
        Assert.Equal(new DateOnly(2026, 1, 5), line.ServiceDate);
    }

    [Fact]
    public void Parse_DependentPatientLoop_SetsPatientSeparatelyFromSubscriber()
    {
        var set = X12TestData.Read(X12TestData.BatchPartial).Groups[0].TransactionSets[0];

        var (message, _) = Claim837PParser.Parse(set);

        Assert.NotNull(message);
        Assert.Equal(["CLM-000124", "CLM-000125"], message.Claims.Select(c => c.ClaimId));
        var first = message.Claims[0];
        Assert.Equal(2, first.ServiceLines.Count);
        Assert.Equal([1, 2], first.ServiceLines[0].DiagnosisPointers);
        Assert.Equal([new DiagnosisCode("ABK", "I10"), new DiagnosisCode("ABF", "E119")], first.Diagnoses);

        var dependent = message.Claims[1];
        Assert.Equal("MBR000789", dependent.Subscriber?.MemberId);
        Assert.Equal(new PersonName("OKAFOR", "GRACE", null), dependent.Patient?.Name);
        Assert.Equal("19", dependent.Patient?.RelationshipCode);
        Assert.Equal(new DateOnly(2001, 3, 11), dependent.Patient?.BirthDate);
        Assert.Null(dependent.Subscriber?.RelationshipCode);
    }

    [Fact]
    public void Parse_MissingPayerAndBadAmount_RejectsWithSegmentAndElementErrors()
    {
        var set = X12TestData.Read(X12TestData.BatchPartial).Groups[0].TransactionSets[1];

        var (message, _) = Claim837PParser.Parse(set);

        Assert.Null(message);
        Assert.False(set.IsAccepted);
        Assert.Contains(TransactionSetErrorCodes.ImplementationSegmentsInError, set.ErrorCodes);

        var missingPayer = Assert.Single(set.SegmentErrors, e => e.LoopId == "2010BB");
        Assert.Equal(("NM1", 10, SegmentErrorCodes.RequiredSegmentMissing), (missingPayer.SegmentId, missingPayer.Position, missingPayer.ErrorCode));

        var badCharge = Assert.Single(set.SegmentErrors, e => e.SegmentId == "SV1");
        Assert.Equal((16, "2400", SegmentErrorCodes.SegmentHasDataElementErrors), (badCharge.Position, badCharge.LoopId, badCharge.ErrorCode));
        Assert.Equal([new X12ElementError("2", ElementErrorCodes.InvalidCharacter)], badCharge.Elements);
    }

    [Fact]
    public void Parse_ClaimTotalDifferentFromLines_IsAWarningNotARejection()
    {
        var body = X12TestData.SingleClaimBody();
        body[body.FindIndex(s => s.StartsWith("CLM*", StringComparison.Ordinal))] = "CLM*CLM-000123*175***11:B:1*Y*A*Y*Y";
        var set = X12TestData.Read(X12TestData.Envelope([.. body])).Groups[0].TransactionSets[0];

        var (message, warnings) = Claim837PParser.Parse(set);

        Assert.NotNull(message);
        Assert.True(set.IsAccepted);
        Assert.Equal("x12.claim_unbalanced", Assert.Single(warnings).Code);
    }

    [Fact]
    public void Parse_MissingServiceDateAndDiagnosis_ReportsRequiredSegments()
    {
        var body = X12TestData.SingleClaimBody().Where(s => !s.StartsWith("DTP*472", StringComparison.Ordinal) && !s.StartsWith("HI*", StringComparison.Ordinal)).ToArray();
        var set = X12TestData.Read(X12TestData.Envelope(body)).Groups[0].TransactionSets[0];

        Claim837PParser.Parse(set);

        Assert.Contains(set.SegmentErrors, e => e is { SegmentId: "HI", LoopId: "2300", ErrorCode: SegmentErrorCodes.RequiredSegmentMissing });
        Assert.Contains(set.SegmentErrors, e => e is { SegmentId: "DTP", LoopId: "2400", ErrorCode: SegmentErrorCodes.RequiredSegmentMissing });
    }

    [Fact]
    public void Parse_UnknownSegment_IsUnexpectedSegment()
    {
        var body = X12TestData.SingleClaimBody();
        body.Insert(3, "ZZZ*1");
        var set = X12TestData.Read(X12TestData.Envelope([.. body])).Groups[0].TransactionSets[0];

        Claim837PParser.Parse(set);

        var error = Assert.Single(set.SegmentErrors);
        Assert.Equal(("ZZZ", 5, SegmentErrorCodes.UnexpectedSegment), (error.SegmentId, error.Position, error.ErrorCode));
    }

    [Fact]
    public void Parse_InvalidCodesAndDates_AreElementErrors()
    {
        var body = X12TestData.SingleClaimBody();
        body[body.FindIndex(s => s.StartsWith("DMG*", StringComparison.Ordinal))] = "DMG*D8*19851314*X";
        var set = X12TestData.Read(X12TestData.Envelope([.. body])).Groups[0].TransactionSets[0];

        Claim837PParser.Parse(set);

        var dmg = Assert.Single(set.SegmentErrors);
        Assert.Equal(
            [new X12ElementError("2", ElementErrorCodes.InvalidDate), new X12ElementError("3", ElementErrorCodes.InvalidCodeValue)],
            dmg.Elements);
    }

    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1245319599", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    public void IsValidNpi_ChecksLuhnDigitWithIssuerPrefix(string npi, bool expected)
    {
        Assert.Equal(expected, Claim837PParser.IsValidNpi(npi));
    }
}
