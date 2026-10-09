using System.Text;
using CareFlow.Ingestion.Domain;

namespace CareFlow.Ingestion.UnitTests.Domain;

public class FileFormatDetectorTests
{
    private const string Isa =
        "ISA*00*          *00*          *ZZ*CAREFLOWCLINIC *ZZ*CAREFLOWPAYER  *260105*0830*^*00501*000000001*0*T*:~";

    [Theory]
    [InlineData("MSH|^~\\&|SYNTHEA|CLINIC|", FileFormat.Hl7v2)]
    [InlineData("\r\n  MSH|^~\\&|SYNTHEA|CLINIC|", FileFormat.Hl7v2)]
    [InlineData("\u000bMSH|^~\\&|SYNTHEA|CLINIC|", FileFormat.Hl7v2)] // MLLP start block
    [InlineData(Isa + "GS*HC~", FileFormat.X12)]
    [InlineData("ISA*00*too-short~", FileFormat.Unknown)]
    [InlineData("MSHX^~\\&|", FileFormat.Unknown)]
    [InlineData("{\"resourceType\":\"Patient\"}", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    public void Detect_ReturnsExpectedFormat(string content, FileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.Detect(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Detect_IgnoresUtf8ByteOrderMark()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, .. "MSH|^~\\&|APP|FAC|"u8];

        Assert.Equal(FileFormat.Hl7v2, FileFormatDetector.Detect(content));
    }

    [Fact]
    public void Detect_X12_RequiresConsistentElementSeparator()
    {
        // Same length as a valid ISA, but the separator before ISA16 does not match position 3.
        var corrupted = Isa[..103] + "|" + Isa[104..];

        Assert.Equal(FileFormat.Unknown, FileFormatDetector.Detect(Encoding.ASCII.GetBytes(corrupted)));
    }
}
