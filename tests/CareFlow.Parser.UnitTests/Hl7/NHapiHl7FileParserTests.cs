using System.Text;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Domain;
using CareFlow.Parser.Infrastructure.Hl7;

namespace CareFlow.Parser.UnitTests.Hl7;

public class NHapiHl7FileParserTests
{
    private static readonly ParseContext Context = new(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Parse_AdtA01Sample_MapsPatientAndEncounter()
    {
        var outcome = Parse(File.ReadAllBytes("samples/hl7/adt-a01.hl7"));

        Assert.True(outcome.IsSuccess, Describe(outcome));
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("ADT^A01", message.MessageType);
        Assert.Equal("MSG00001", message.ControlId);
        Assert.Equal("2.5.1", message.Version);
        Assert.Equal("CAREFLOW-CLINIC", message.Sender);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero), message.CreatedAt);

        var patient = message.Patient!;
        Assert.Equal(new Identifier("PAT-000123", "CAREFLOW", "MR"), Assert.Single(patient.Identifiers));
        Assert.Equal(new PersonName("Rivera", "Ana", "M"), patient.Name);
        Assert.Equal(new DateOnly(1985, 2, 14), patient.BirthDate);
        Assert.Equal("F", patient.Sex);
        Assert.Equal(new PostalAddress("123 Main St", null, "Orlando", "FL", "32801", "USA"), patient.Address);

        var encounter = message.Encounter!;
        Assert.Equal("I", encounter.PatientClass);
        Assert.Equal("WARD-3/301/A", encounter.Location);
        Assert.Equal("CAREFLOW", encounter.Facility);
        Assert.Equal("MED", encounter.HospitalService);
        Assert.Equal(new PractitionerRef("DOC-77", new PersonName("Mora", "Luis", null)), encounter.AttendingProvider);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero), encounter.AdmittedAt); // from EVN-2
    }

    [Fact]
    public void Parse_OruR01Sample_MapsObservationsWithOffsets()
    {
        var outcome = Parse(File.ReadAllBytes("samples/hl7/oru-r01.hl7"));

        Assert.True(outcome.IsSuccess, Describe(outcome));
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("ORU^R01", message.MessageType);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 10, 15, 0, TimeSpan.FromHours(-5)), message.CreatedAt);
        Assert.Equal("O", message.Encounter?.PatientClass);
        Assert.Equal(3, message.Observations.Count);

        var creatinine = message.Observations[1];
        Assert.Equal(2, creatinine.SetId);
        Assert.Equal("24323-8", creatinine.OrderCode);
        Assert.Equal("2160-0", creatinine.Code);
        Assert.Equal("LN", creatinine.CodeSystem);
        Assert.Equal("Creatinine", creatinine.Display);
        Assert.Equal("NM", creatinine.ValueType);
        Assert.Equal("1.4", creatinine.Value);
        Assert.Equal("mg/dL", creatinine.Units);
        Assert.Equal("0.6-1.2", creatinine.ReferenceRange);
        Assert.Equal("H", creatinine.AbnormalFlag);
        Assert.Equal("F", creatinine.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.FromHours(-5)), creatinine.ObservedAt);
    }

    [Fact]
    public void Parse_BatchFileWithCrlf_ReturnsEachMessage()
    {
        var outcome = Parse(File.ReadAllBytes("samples/hl7/adt-batch.hl7"));

        Assert.True(outcome.IsSuccess, Describe(outcome));
        Assert.Equal(["ADT^A08", "ADT^A04"], outcome.Messages.Select(m => m.MessageType));
        Assert.Equal(["MSG00003", "MSG00004"], outcome.Messages.Select(m => m.ControlId));
        Assert.Equal("E", outcome.Messages[1].Encounter?.PatientClass);
        Assert.Empty(outcome.Issues);
    }

    [Fact]
    public void Parse_MllpFramedMessage_IsUnwrapped()
    {
        var framed = "\u000b" + File.ReadAllText("samples/hl7/adt-a01.hl7") + "\u001c\r";

        Assert.True(Parse(Encoding.UTF8.GetBytes(framed)).IsSuccess);
    }

    [Fact]
    public void Parse_OneBadMessageInFile_RejectsOnlyThatMessage()
    {
        var good = File.ReadAllText("samples/hl7/adt-a01.hl7");
        var missingIdentifier = good.Replace("MSG00001", "MSG00009", StringComparison.Ordinal)
            .Replace("PAT-000123^^^CAREFLOW^MR", string.Empty, StringComparison.Ordinal);

        var outcome = Parse(Encoding.UTF8.GetBytes(good + missingIdentifier));

        Assert.True(outcome.IsSuccess);
        Assert.Equal("MSG00001", Assert.Single(outcome.Messages).ControlId);
        Assert.Equal(1, outcome.RejectedCount);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal(("hl7.required_field_missing", "PID-3", "MSG00009"), (issue.Code, issue.Location, issue.ControlId));
    }

    [Fact]
    public void Parse_UnsupportedMessageType_IsRejected()
    {
        var outcome = Parse("MSH|^~\\&|APP|FAC|CAREFLOW|CAREFLOW|20260105083000||SIU^S12|MSG1|P|2.5.1\rSCH|1\r"u8.ToArray());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("hl7.no_accepted_messages", outcome.FailureCode);
        Assert.Equal("hl7.unsupported_message_type", Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public void Parse_NoMshSegment_Fails()
    {
        var outcome = Parse("PID|1||PAT-1\r"u8.ToArray());

        Assert.Equal("hl7.no_messages", outcome.FailureCode);
    }

    [Fact]
    public void Parse_IssuesNeverEchoFieldValues()
    {
        // A malformed date in PID-7 makes NHapi throw with the value in its message.
        var content = File.ReadAllText("samples/hl7/adt-a01.hl7").Replace("19850214", "1985-02-14-SECRET", StringComparison.Ordinal);

        var outcome = Parse(Encoding.UTF8.GetBytes(content));

        Assert.All(outcome.Issues, i => Assert.DoesNotContain("SECRET", i.Location + i.Description, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20260105", 2026, 1, 5, 0, 0, 0)]
    [InlineData("202601050830", 2026, 1, 5, 8, 30, 0)]
    [InlineData("20260105083015-0500", 2026, 1, 5, 8, 30, -5)]
    [InlineData("20260105083015.25+0130", 2026, 1, 5, 8, 30, 1.5)]
    public void Timestamp_ParsesHl7Precision(string value, int year, int month, int day, int hour, int minute, double offsetHours)
    {
        var parsed = Hl7Values.Timestamp(value);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(year, month, day, hour, minute, 0), new DateTime(parsed.Value.Year, parsed.Value.Month, parsed.Value.Day, parsed.Value.Hour, parsed.Value.Minute, 0));
        Assert.Equal(TimeSpan.FromHours(offsetHours), parsed.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-01-05")]
    [InlineData("20261305")]
    [InlineData("20260105+05")]
    public void Timestamp_InvalidValues_ReturnNull(string value)
    {
        Assert.Null(Hl7Values.Timestamp(value));
    }

    private static ParseOutcome Parse(byte[] content) => new NHapiHl7FileParser().Parse(content, Context);

    private static string Describe(ParseOutcome outcome) =>
        string.Join("; ", outcome.Issues.Select(i => $"{i.Code} at {i.Location}: {i.Description}"));
}
