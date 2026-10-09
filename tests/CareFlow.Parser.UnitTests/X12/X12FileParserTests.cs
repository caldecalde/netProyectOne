using System.Text;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Application.Parsing;

namespace CareFlow.Parser.UnitTests.X12;

public class X12FileParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid IngestionId = Guid.Parse("7b0a3c5e-1f2d-4e6a-8b9c-0d1e2f3a4b5c");

    [Fact]
    public void Parse_SingleClaim_AcceptsAndProducesAccepting999()
    {
        var outcome = Parse(X12TestData.SingleClaim);

        Assert.True(outcome.IsSuccess);
        Assert.Single(outcome.Messages);
        Assert.Equal(0, outcome.RejectedCount);

        var control = X12FileParser.AckControlNumber(IngestionId).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
        var expected =
            $"ISA*00*          *00*          *ZZ*CAREFLOWPAYER  *ZZ*CAREFLOWCLINIC *260105*1400*^*00501*{control}*0*T*:~" +
            "GS*FA*CAREFLOWPAYER*CAREFLOWCLINIC*20260105*1400*1*X*005010X231A1~" +
            "ST*999*0001*005010X231A1~" +
            "AK1*HC*1*005010X222A1~" +
            "AK2*837*0001*005010X222A1~" +
            "IK5*A~" +
            "AK9*A*1*1*1~" +
            "SE*6*0001~" +
            "GE*1*1~" +
            $"IEA*1*{control}~";
        Assert.Equal(expected, outcome.Acknowledgment);
    }

    [Fact]
    public void Parse_OneGoodOneBadTransactionSet_PartiallyAccepts()
    {
        var outcome = Parse(X12TestData.BatchPartial);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("0001", Assert.Single(outcome.Messages).ControlId);
        Assert.Equal(1, outcome.RejectedCount);
        Assert.All(outcome.Issues, i => Assert.Equal("0002", i.ControlId));

        var ack = outcome.Acknowledgment!.Split('~');
        Assert.Contains("AK2*837*0001*005010X222A1", ack);
        Assert.Contains("AK2*837*0002*005010X222A1", ack);
        Assert.Contains("IK3*NM1*10*2010BB*3", ack);
        Assert.Contains("IK3*SV1*16*2400*8", ack);
        Assert.Contains("IK4*2**6", ack);
        Assert.Contains("IK5*R*I5", ack);
        Assert.Contains("AK9*P*2*2*1", ack);
    }

    [Fact]
    public void Parse_IssuesNeverEchoFieldValues()
    {
        var outcome = Parse(X12TestData.BatchPartial);

        // The rejected set carries a patient name and member ID; neither may leak into issue text or the 999.
        var text = string.Join('\n', outcome.Issues.Select(i => i.Location + " " + i.Description)) + outcome.Acknowledgment;
        Assert.DoesNotContain("RIVERA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MBR000123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnsupportedGroupVersion_RejectsGroupWithoutTransactionDetail()
    {
        var content = X12TestData.SingleClaim.Replace("*X*005010X222A1~", "*X*004010X098A1~", StringComparison.Ordinal);

        var outcome = Parse(content);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("x12.no_accepted_transactions", outcome.FailureCode);
        var ack = outcome.Acknowledgment!.Split('~');
        Assert.Contains("AK9*R*1*1*0*2", ack);
        Assert.DoesNotContain(ack, s => s.StartsWith("AK2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NotAnInterchange_FailsWithoutAcknowledgment()
    {
        var outcome = Parse("GS*HC*A*B~ST*837*0001~");

        Assert.Equal("x12.isa_missing", outcome.FailureCode);
        Assert.Null(outcome.Acknowledgment);
    }

    [Fact]
    public void Parse_BrokenInterchangeTrailer_FailsWithoutAcknowledgment()
    {
        var outcome = Parse(X12TestData.SingleClaim.Replace("IEA*1*000000001", "IEA*1*000000999", StringComparison.Ordinal));

        Assert.Equal("x12.interchange_invalid", outcome.FailureCode);
        Assert.Null(outcome.Acknowledgment);
        Assert.Equal("x12.interchange_control_mismatch", Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public void AckControlNumber_IsStableAndInRange()
    {
        var first = X12FileParser.AckControlNumber(IngestionId);

        Assert.Equal(first, X12FileParser.AckControlNumber(IngestionId));
        Assert.InRange(first, 1, 999_999_999);
    }

    private static Domain.ParseOutcome Parse(string content) =>
        new X12FileParser().Parse(Encoding.ASCII.GetBytes(content), new ParseContext(IngestionId, Now));
}
