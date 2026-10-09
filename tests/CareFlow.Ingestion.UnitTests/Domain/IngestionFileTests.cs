using CareFlow.Ingestion.Domain;

namespace CareFlow.Ingestion.UnitTests.Domain;

public class IngestionFileTests
{
    private static readonly string ValidSha = new('a', 64);
    private static readonly Guid Id = Guid.Parse("8f1d3c52-0d6e-4f7b-9a43-3b1c2d4e5f60");

    [Fact]
    public void Receive_BuildsDatePartitionedStorageKey_FromIdAndFormat()
    {
        var file = IngestionFile.Receive(Id, "adt.hl7", FileFormat.Hl7v2, 10, ValidSha, "user-1", new DateTimeOffset(2026, 1, 5, 23, 30, 0, TimeSpan.FromHours(-6)));

        // 23:30 at UTC-6 is the next day in UTC.
        Assert.Equal("raw/2026/01/06/8f1d3c520d6e4f7b9a433b1c2d4e5f60.hl7", file.StorageKey);
        Assert.Equal(TimeSpan.Zero, file.ReceivedAt.Offset);
        Assert.Equal(IngestionStatus.Received, file.Status);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\clinic\\claims.x12", "claims.x12")]
    [InlineData("claim\u0000s.x12", "claims.x12")]
    [InlineData("   ", "upload")]
    [InlineData(null, "upload")]
    public void SanitizeFileName_StripsPathsAndControlCharacters(string? input, string expected)
    {
        Assert.Equal(expected, IngestionFile.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongNames()
    {
        Assert.Equal(IngestionFile.MaxFileNameLength, IngestionFile.SanitizeFileName(new string('x', 1000)).Length);
    }

    [Fact]
    public void Receive_RejectsUnknownFormat()
    {
        Assert.Throws<ArgumentException>(() =>
            IngestionFile.Receive(Id, "f", FileFormat.Unknown, 10, ValidSha, "user-1", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase
    public void Receive_RejectsMalformedHash(string sha)
    {
        Assert.Throws<ArgumentException>(() =>
            IngestionFile.Receive(Id, "f", FileFormat.X12, 10, sha, "user-1", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkFailed_RecordsReason_AndForbidsFurtherTransitions()
    {
        var file = IngestionFile.Receive(Id, "f", FileFormat.X12, 10, ValidSha, "user-1", DateTimeOffset.UtcNow);

        file.MarkFailed("ISA segment truncated");

        Assert.Equal(IngestionStatus.Failed, file.Status);
        Assert.Equal("ISA segment truncated", file.FailureReason);
        Assert.Throws<InvalidOperationException>(file.MarkParsed);
    }
}
