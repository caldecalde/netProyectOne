using CareFlow.Parser.Domain.X12;

namespace CareFlow.Parser.UnitTests.X12;

public class X12EnvelopeReaderTests
{
    [Fact]
    public void Read_ValidInterchange_BuildsGroupsAndTransactionSets()
    {
        var interchange = X12TestData.Read(X12TestData.BatchPartial);

        Assert.Empty(interchange.Errors);
        Assert.Equal("000000002", interchange.ControlNumber);
        var group = Assert.Single(interchange.Groups);
        Assert.Equal("005010X222A1", group.Version);
        Assert.Empty(group.ErrorCodes);
        Assert.Equal(["0001", "0002"], group.TransactionSets.Select(t => t.ControlNumber));
        Assert.All(group.TransactionSets, t => Assert.Empty(t.ErrorCodes));
        Assert.Equal(45, group.TransactionSets[0].Segments.Count);
    }

    [Fact]
    public void Read_SegmentCountMismatch_IsReportedOnTheTransactionSet()
    {
        var content = X12TestData.SingleClaim.Replace("SE*24*0001", "SE*23*0001", StringComparison.Ordinal);

        var set = X12TestData.Read(content).Groups[0].TransactionSets[0];

        Assert.Equal([TransactionSetErrorCodes.SegmentCountMismatch], set.ErrorCodes);
        Assert.False(set.IsAccepted);
    }

    [Fact]
    public void Read_SeControlNumberMismatch_IsReported()
    {
        var content = X12TestData.SingleClaim.Replace("SE*24*0001", "SE*24*0009", StringComparison.Ordinal);

        Assert.Contains(TransactionSetErrorCodes.ControlNumberMismatch, X12TestData.Read(content).Groups[0].TransactionSets[0].ErrorCodes);
    }

    [Fact]
    public void Read_MissingSe_IsReportedAsTrailerMissing()
    {
        var content = X12TestData.SingleClaim.Replace("SE*24*0001~\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains(TransactionSetErrorCodes.TrailerMissing, X12TestData.Read(content).Groups[0].TransactionSets[0].ErrorCodes);
    }

    [Theory]
    [InlineData("GE*1*1~", "GE*2*1~", GroupErrorCodes.TransactionSetCountMismatch)]
    [InlineData("GE*1*1~", "GE*1*7~", GroupErrorCodes.ControlNumberMismatch)]
    public void Read_GroupTrailerMismatch_IsReportedOnTheGroup(string original, string replacement, string expectedCode)
    {
        var content = X12TestData.SingleClaim.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Contains(expectedCode, X12TestData.Read(content).Groups[0].ErrorCodes);
    }

    [Theory]
    [InlineData("IEA*1*000000001~", "IEA*1*000000009~", "x12.interchange_control_mismatch")]
    [InlineData("IEA*1*000000001~", "IEA*2*000000001~", "x12.group_count_mismatch")]
    [InlineData("IEA*1*000000001~", "", "x12.iea_missing")]
    public void Read_InterchangeTrailerProblems_AreInterchangeErrors(string original, string replacement, string expectedCode)
    {
        var content = X12TestData.SingleClaim.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Contains(X12TestData.Read(content).Errors, e => e.Code == expectedCode);
    }
}
