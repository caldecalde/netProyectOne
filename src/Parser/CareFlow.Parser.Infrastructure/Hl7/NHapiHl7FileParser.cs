using System.Text;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Domain;
using NHapi.Base;
using NHapi.Base.Model;
using NHapi.Base.Parser;
using NHapi.Model.V251.Datatype;
using NHapi.Model.V251.Message;
using NHapi.Model.V251.Segment;

namespace CareFlow.Parser.Infrastructure.Hl7;

/// <summary>
/// Parses HL7 v2 files with NHapi. A file may hold several messages (with or without FHS/BHS batch envelopes, CR or
/// CRLF segment endings, MLLP framing). Each message is parsed on its own, so one bad message rejects only itself.
/// Supported: ADT^A01/A04/A08 (patient + encounter) and ORU^R01 (patient + observations), read with the v2.5.1 model.
/// </summary>
internal sealed class NHapiHl7FileParser : IFileParser
{
    /// <summary>
    /// Messages are read with the 2.5.1 model whatever their MSH-12 says. Earlier 2.x versions are backward compatible
    /// for the fields mapped here, and pinning one model keeps the mapping in one place.
    /// </summary>
    private const string ModelVersion = "2.5.1";

    private static readonly HashSet<string> SupportedTypes = ["ADT^A01", "ADT^A04", "ADT^A08", "ORU^R01"];

    private static readonly HashSet<string> BatchSegments = ["FHS", "BHS", "BTS", "FTS"];

    public string Format => "Hl7v2";

    public ParseOutcome Parse(ReadOnlyMemory<byte> content, ParseContext context)
    {
        var issues = new List<ParseIssue>();
        var rawMessages = SplitMessages(Encoding.UTF8.GetString(content.Span), issues);
        if (rawMessages.Count == 0)
        {
            issues.Add(new ParseIssue(IssueSeverity.Error, "hl7.no_messages", "MSH", "The file contains no MSH segment."));
            return ParseOutcome.Failure("hl7.no_messages", [.. issues]);
        }

        // PipeParser is not documented as thread-safe; one per file is cheap (model classes are cached by NHapi).
        var parser = new PipeParser();
        var messages = new List<ParsedMessage>();
        foreach (var raw in rawMessages)
        {
            var messageIssues = new List<ParseIssue>();
            var message = ParseMessage(parser, raw, messageIssues);
            issues.AddRange(messageIssues);
            if (message is not null && messageIssues.TrueForAll(i => i.Severity != IssueSeverity.Error))
            {
                messages.Add(message);
            }
        }

        return new ParseOutcome
        {
            Messages = messages,
            RejectedCount = rawMessages.Count - messages.Count,
            Issues = issues,
            FailureCode = messages.Count == 0 ? "hl7.no_accepted_messages" : null,
        };
    }

    private static ParsedMessage? ParseMessage(PipeParser parser, string raw, List<ParseIssue> issues)
    {
        // Read MSH-9 and MSH-10 before NHapi does, so even unparseable messages can be identified in issues.
        var msh = raw[..raw.IndexOf('\r', StringComparison.Ordinal)];
        var fields = msh.Length > 3 ? msh.Split(msh[3]) : [];
        var controlId = Field(fields, 10);
        var componentSeparator = fields.Length > 1 && fields[1].Length > 0 ? fields[1][0] : '^';
        var typeParts = Field(fields, 9).Split(componentSeparator);
        var messageType = typeParts.Length >= 2 ? $"{typeParts[0]}^{typeParts[1]}" : typeParts[0];

        void Error(string code, string location, string description) =>
            issues.Add(new ParseIssue(IssueSeverity.Error, code, location, description) { ControlId = NullIfEmpty(controlId) });

        if (controlId.Length == 0)
        {
            Error("hl7.required_field_missing", "MSH-10", "Message control ID is required.");
        }

        if (!SupportedTypes.Contains(messageType))
        {
            Error("hl7.unsupported_message_type", "MSH-9", $"Message type '{Sanitize(messageType)}' is not supported; expected ADT^A01, ADT^A04, ADT^A08 or ORU^R01.");
            return null;
        }

        IMessage parsed;
        try
        {
            parsed = parser.Parse(raw, ModelVersion);
        }
        catch (HL7Exception ex)
        {
            // The exception message can quote field values (PHI), so only its position and error code are kept.
            var location = string.IsNullOrEmpty(ex.SegmentName) ? "message" : ex.FieldPosition > 0 ? $"{ex.SegmentName}-{ex.FieldPosition}" : ex.SegmentName;
            Error("hl7.syntax_error", location, $"The message could not be parsed ({ex.ErrorCode}).");
            return null;
        }

        var header = (MSH)parsed.GetStructure("MSH");
        var result = new ParsedMessage
        {
            MessageType = messageType,
            ControlId = controlId,
            Version = Hl7Values.Text(header.VersionID.VersionID.Value),
            CreatedAt = Hl7Values.Timestamp(header.DateTimeOfMessage.Time.Value),
            Sender = Hl7Values.Text(header.SendingFacility.NamespaceID.Value) ?? Hl7Values.Text(header.SendingApplication.NamespaceID.Value),
            Receiver = Hl7Values.Text(header.ReceivingFacility.NamespaceID.Value) ?? Hl7Values.Text(header.ReceivingApplication.NamespaceID.Value),
        };

        return parsed switch
        {
            ADT_A01 adt => MapAdt(adt, result, Error),
            ORU_R01 oru => MapOru(oru, result, issues, controlId, Error),
            _ => null,
        };
    }

    private static ParsedMessage MapAdt(ADT_A01 adt, ParsedMessage message, Action<string, string, string> error)
    {
        var patient = MapPatient(adt.PID, error);
        if (Hl7Values.Text(adt.PV1.PatientClass.Value) is null)
        {
            error("hl7.required_field_missing", "PV1-2", "Patient class is required for ADT messages.");
        }

        return message with
        {
            Patient = patient,
            Encounter = MapEncounter(adt.PV1, Hl7Values.Timestamp(adt.EVN.RecordedDateTime.Time.Value)),
        };
    }

    private static ParsedMessage MapOru(ORU_R01 oru, ParsedMessage message, List<ParseIssue> issues, string controlId, Action<string, string, string> error)
    {
        if (oru.PATIENT_RESULTRepetitionsUsed == 0)
        {
            error("hl7.required_segment_missing", "PID", "ORU^R01 has no patient result group.");
            return message;
        }

        if (oru.PATIENT_RESULTRepetitionsUsed > 1)
        {
            issues.Add(new ParseIssue(IssueSeverity.Warning, "hl7.multiple_patients", "PATIENT_RESULT", "Only the first patient result group is mapped.") { ControlId = controlId });
        }

        var result = oru.GetPATIENT_RESULT(0);
        var observations = new List<ObservationRecord>();
        for (var o = 0; o < result.ORDER_OBSERVATIONRepetitionsUsed; o++)
        {
            var order = result.GetORDER_OBSERVATION(o);
            for (var x = 0; x < order.OBSERVATIONRepetitionsUsed; x++)
            {
                if (MapObservation(order.OBR, order.GetOBSERVATION(x).OBX, error) is { } observation)
                {
                    observations.Add(observation);
                }
            }
        }

        if (observations.Count == 0)
        {
            error("hl7.required_segment_missing", "OBX", "ORU^R01 contains no observations.");
        }

        var visit = result.PATIENT.VISIT.PV1;
        return message with
        {
            Patient = MapPatient(result.PATIENT.PID, error),
            Encounter = Hl7Values.Text(visit.PatientClass.Value) is null ? null : MapEncounter(visit, admittedAt: null),
            Observations = observations,
        };
    }

    private static PatientRecord MapPatient(PID pid, Action<string, string, string> error)
    {
        var identifiers = pid.GetPatientIdentifierList()
            .Where(cx => Hl7Values.Text(cx.IDNumber.Value) is not null)
            .Select(cx => new Identifier(cx.IDNumber.Value.Trim(), Hl7Values.Text(cx.AssigningAuthority.NamespaceID.Value), Hl7Values.Text(cx.IdentifierTypeCode.Value)))
            .ToArray();
        if (identifiers.Length == 0)
        {
            error("hl7.required_field_missing", "PID-3", "At least one patient identifier is required.");
        }

        var name = pid.GetPatientName().FirstOrDefault();
        if (name is null || Hl7Values.Text(name.FamilyName.Surname.Value) is null)
        {
            error("hl7.required_field_missing", "PID-5", "Patient family name is required.");
        }

        var address = pid.GetPatientAddress().FirstOrDefault();
        var phone = pid.GetPhoneNumberHome().FirstOrDefault();
        return new PatientRecord
        {
            Identifiers = identifiers,
            Name = name is null ? null : new PersonName(
                Hl7Values.Text(name.FamilyName.Surname.Value),
                Hl7Values.Text(name.GivenName.Value),
                Hl7Values.Text(name.SecondAndFurtherGivenNamesOrInitialsThereof.Value)),
            BirthDate = Hl7Values.Date(pid.DateTimeOfBirth.Time.Value),
            Sex = Hl7Values.Text(pid.AdministrativeSex.Value),
            Address = address is null ? null : MapAddress(address),
            Phone = phone is null ? null : Hl7Values.Text(phone.TelephoneNumber.Value)
                ?? Hl7Values.Text($"{phone.AreaCityCode.Value}{phone.LocalNumber.Value}"),
        };
    }

    private static PostalAddress? MapAddress(XAD address)
    {
        var result = new PostalAddress(
            Hl7Values.Text(address.StreetAddress.StreetOrMailingAddress.Value),
            Hl7Values.Text(address.OtherDesignation.Value),
            Hl7Values.Text(address.City.Value),
            Hl7Values.Text(address.StateOrProvince.Value),
            Hl7Values.Text(address.ZipOrPostalCode.Value),
            Hl7Values.Text(address.Country.Value));
        return result == new PostalAddress(null, null, null, null, null, null) ? null : result;
    }

    private static EncounterRecord MapEncounter(PV1 pv1, DateTimeOffset? admittedAt)
    {
        var location = pv1.AssignedPatientLocation;
        var attending = pv1.GetAttendingDoctor().FirstOrDefault();
        var locationParts = new[] { location.PointOfCare.Value, location.Room.Value, location.Bed.Value }
            .Select(Hl7Values.Text)
            .Where(p => p is not null);

        return new EncounterRecord
        {
            VisitNumber = Hl7Values.Text(pv1.VisitNumber.IDNumber.Value),
            PatientClass = Hl7Values.Text(pv1.PatientClass.Value),
            Location = Hl7Values.Text(string.Join('/', locationParts)),
            Facility = Hl7Values.Text(location.Facility.NamespaceID.Value),
            HospitalService = Hl7Values.Text(pv1.HospitalService.Value),
            AttendingProvider = attending is null || Hl7Values.Text(attending.IDNumber.Value) is null && Hl7Values.Text(attending.FamilyName.Surname.Value) is null
                ? null
                : new PractitionerRef(
                    Hl7Values.Text(attending.IDNumber.Value),
                    new PersonName(Hl7Values.Text(attending.FamilyName.Surname.Value), Hl7Values.Text(attending.GivenName.Value), null)),
            AdmittedAt = Hl7Values.Timestamp(pv1.AdmitDateTime.Time.Value) ?? admittedAt,
            DischargedAt = Hl7Values.Timestamp(pv1.GetDischargeDateTime().FirstOrDefault()?.Time.Value),
        };
    }

    private static ObservationRecord? MapObservation(OBR obr, OBX obx, Action<string, string, string> error)
    {
        var code = Hl7Values.Text(obx.ObservationIdentifier.Identifier.Value);
        if (code is null)
        {
            error("hl7.required_field_missing", "OBX-3", "Observation identifier is required.");
            return null;
        }

        var status = Hl7Values.Text(obx.ObservationResultStatus.Value);
        if (status is null)
        {
            error("hl7.required_field_missing", "OBX-11", "Observation result status is required.");
        }

        var value = obx.GetObservationValue().Length == 0
            ? null
            : Hl7Values.Text(string.Join('~', obx.GetObservationValue().Select(v => Hl7Values.Render(v.Data) ?? string.Empty)));
        if (value is not null && Hl7Values.Text(obx.ValueType.Value) is null)
        {
            error("hl7.required_field_missing", "OBX-2", "Value type is required when a value is present.");
        }

        return new ObservationRecord
        {
            SetId = int.TryParse(obx.SetIDOBX.Value, out var setId) ? setId : 0,
            OrderCode = Hl7Values.Text(obr.UniversalServiceIdentifier.Identifier.Value),
            OrderName = Hl7Values.Text(obr.UniversalServiceIdentifier.Text.Value),
            Code = code,
            CodeSystem = Hl7Values.Text(obx.ObservationIdentifier.NameOfCodingSystem.Value),
            Display = Hl7Values.Text(obx.ObservationIdentifier.Text.Value),
            ValueType = Hl7Values.Text(obx.ValueType.Value),
            Value = value,
            Units = Hl7Values.Text(obx.Units.Identifier.Value),
            ReferenceRange = Hl7Values.Text(obx.ReferencesRange.Value),
            AbnormalFlag = Hl7Values.Text(obx.GetAbnormalFlags().FirstOrDefault()?.Value),
            Status = status,
            ObservedAt = Hl7Values.Timestamp(obx.DateTimeOfTheObservation.Time.Value) ?? Hl7Values.Timestamp(obr.ObservationDateTime.Time.Value),
        };
    }

    /// <summary>
    /// Splits a file into messages of CR-separated segments: removes MLLP framing and batch envelope segments and
    /// accepts CR, LF or CRLF line endings.
    /// </summary>
    internal static List<string> SplitMessages(string text, List<ParseIssue> issues)
    {
        var lines = text
            .Replace("\u000b", string.Empty, StringComparison.Ordinal)
            .Replace("\u001c", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r')
            .Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var messages = new List<string>();
        StringBuilder? current = null;
        foreach (var line in lines)
        {
            var segmentId = line.Length >= 3 ? line[..3] : line;
            if (BatchSegments.Contains(segmentId))
            {
                continue;
            }

            if (segmentId == "MSH")
            {
                if (current is not null)
                {
                    messages.Add(current.ToString());
                }

                current = new StringBuilder();
            }
            else if (current is null)
            {
                issues.Add(new ParseIssue(IssueSeverity.Warning, "hl7.data_before_msh", segmentId, "Segments before the first MSH were ignored."));
                continue;
            }

            current.Append(line).Append('\r');
        }

        if (current is not null)
        {
            messages.Add(current.ToString());
        }

        return messages;
    }

    private static string Field(string[] fields, int number) => fields.Length >= number ? fields[number - 1] : string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>Message types are codes, but they come from the file; keep only code characters before echoing one.</summary>
    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '^' or '_').Take(20).ToArray());
}
