using System.Text;
using CareFlow.Parser.Domain.X12;

namespace CareFlow.Parser.UnitTests.X12;

public class X12TokenizerTests
{
    internal const string Isa = "ISA*00*          *00*          *ZZ*CAREFLOWCLINIC *ZZ*CAREFLOWPAYER  *260105*0830*^*00501*000000001*0*T*:~";

    [Fact]
    public void Tokenize_ReadsDelimitersFromIsa()
    {
        var result = X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(Isa + "GS*HC*A*B~"));

        Assert.True(result.IsSuccess);
        Assert.Equal(new X12Delimiters('*', ':', '^', '~'), result.Delimiters);
        Assert.Equal(["ISA", "GS"], result.Segments.Select(s => s.Id));
        Assert.Equal("CAREFLOWCLINIC ", result.Segments[0][6]);
        Assert.Equal(2, result.Segments[1].Ordinal);
    }

    [Fact]
    public void Tokenize_HonorsNonDefaultDelimiters()
    {
        // Same interchange with '|' elements, '>' components, '!' repetitions and a newline as segment terminator.
        var isa = Isa.Replace('*', '|').Replace(':', '>').Replace('^', '!').Replace('~', '\n');
        var result = X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(isa + "SV1|HC>99213>25|150|UN|1\r\n"));

        Assert.True(result.IsSuccess);
        var sv1 = result.Segments[1];
        Assert.Equal("SV1", sv1.Id);
        Assert.Equal("99213", sv1.Component(1, 2));
        Assert.Equal("25", sv1.Component(1, 3));
        Assert.Equal("1", sv1[4]); // the CR before the LF terminator is not part of the data
    }

    [Fact]
    public void Tokenize_SkipsLineBreaksBetweenSegmentsAndToleratesBom()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("\r\n" + Isa + "\r\nGS*HC~\r\n\r\nST*837*0001~\n")];

        var result = X12Tokenizer.Tokenize(content);

        Assert.Equal(["ISA", "GS", "ST"], result.Segments.Select(s => s.Id));
        Assert.Equal("0001", result.Segments[2][2]);
    }

    [Fact]
    public void Segment_MissingElementsReadAsEmpty()
    {
        var segment = X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(Isa + "NM1*41*2~")).Segments[1];

        Assert.Equal(2, segment.ElementCount);
        Assert.Equal(string.Empty, segment[9]);
        Assert.False(segment.Has(9));
        Assert.Equal(string.Empty, segment.Component(5, 3));
    }

    [Theory]
    [InlineData("GS*HC*A*B~", "x12.isa_missing")]
    [InlineData("ISA*00*short~", "x12.isa_missing")]
    public void Tokenize_WithoutIsa_Fails(string content, string expectedCode)
    {
        Assert.Equal(expectedCode, X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(content)).ErrorCode);
    }

    [Fact]
    public void Tokenize_IsaWithShiftedField_FailsAsMalformed()
    {
        // ISA06 one character short: every later separator is off by one.
        var shifted = Isa.Replace("CAREFLOWCLINIC *", "CAREFLOWCLINIC*", StringComparison.Ordinal) + " ";

        Assert.Equal("x12.isa_malformed", X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(shifted)).ErrorCode);
    }
}
