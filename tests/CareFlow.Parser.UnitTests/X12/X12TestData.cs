using System.Text;
using CareFlow.Parser.Domain.X12;

namespace CareFlow.Parser.UnitTests.X12;

internal static class X12TestData
{
    public static readonly string SingleClaim = File.ReadAllText("samples/x12/837p-single-claim.x12");

    public static readonly string BatchPartial = File.ReadAllText("samples/x12/837p-batch-partial.x12");

    public static X12Interchange Read(string content) =>
        X12EnvelopeReader.Read(X12Tokenizer.Tokenize(Encoding.ASCII.GetBytes(content)));

    /// <summary>Wraps transaction set segments in a valid ISA/GS envelope, computing SE01 from the content.</summary>
    public static string Envelope(params string[] transactionBody)
    {
        var segments = new List<string> { "ST*837*0001*005010X222A1" };
        segments.AddRange(transactionBody);
        segments.Add($"SE*{segments.Count + 1}*0001");
        return X12TokenizerTests.Isa
            + "GS*HC*CAREFLOWCLINIC*CAREFLOWPAYER*20260105*0830*1*X*005010X222A1~"
            + string.Concat(segments.Select(s => s + "~"))
            + "GE*1*1~IEA*1*000000001~";
    }

    /// <summary>The transaction body of the single-claim sample (between ST and SE), for building variants.</summary>
    public static List<string> SingleClaimBody() =>
        SingleClaim.Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SkipWhile(s => !s.StartsWith("ST*", StringComparison.Ordinal)).Skip(1)
            .TakeWhile(s => !s.StartsWith("SE*", StringComparison.Ordinal))
            .ToList();
}
