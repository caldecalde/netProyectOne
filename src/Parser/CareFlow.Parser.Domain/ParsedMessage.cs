namespace CareFlow.Parser.Domain;

// Normalized, format-independent view of what a healthcare message says. HL7 v2 messages and X12 transaction sets
// both map onto these records, so downstream services (Records) never deal with segment syntax.

/// <summary>One accepted HL7 message or X12 transaction set.</summary>
public sealed record ParsedMessage
{
    /// <summary><c>ADT^A01</c>, <c>ORU^R01</c>, <c>837P</c>, ...</summary>
    public required string MessageType { get; init; }

    /// <summary>MSH-10 message control ID, or ST02 transaction set control number.</summary>
    public required string ControlId { get; init; }

    /// <summary>HL7 version (MSH-12) or X12 implementation guide (ST03).</summary>
    public string? Version { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? Sender { get; init; }

    public string? Receiver { get; init; }

    public PatientRecord? Patient { get; init; }

    public EncounterRecord? Encounter { get; init; }

    public IReadOnlyList<ObservationRecord> Observations { get; init; } = [];

    public IReadOnlyList<ClaimRecord> Claims { get; init; } = [];
}

public sealed record Identifier(string Value, string? System, string? Type);

public sealed record PersonName(string? Family, string? Given, string? Middle);

public sealed record PostalAddress(string? Line1, string? Line2, string? City, string? State, string? PostalCode, string? Country);

public sealed record PatientRecord
{
    public IReadOnlyList<Identifier> Identifiers { get; init; } = [];

    public PersonName? Name { get; init; }

    public DateOnly? BirthDate { get; init; }

    /// <summary>Administrative sex as sent (<c>F</c>, <c>M</c>, <c>U</c>, ...).</summary>
    public string? Sex { get; init; }

    public PostalAddress? Address { get; init; }

    public string? Phone { get; init; }
}

public sealed record PractitionerRef(string? Id, PersonName? Name);

public sealed record EncounterRecord
{
    public string? VisitNumber { get; init; }

    /// <summary>PV1-2: <c>I</c> inpatient, <c>O</c> outpatient, <c>E</c> emergency, ...</summary>
    public string? PatientClass { get; init; }

    public string? Location { get; init; }

    public string? Facility { get; init; }

    public string? HospitalService { get; init; }

    public PractitionerRef? AttendingProvider { get; init; }

    public DateTimeOffset? AdmittedAt { get; init; }

    public DateTimeOffset? DischargedAt { get; init; }
}

public sealed record ObservationRecord
{
    public required int SetId { get; init; }

    public string? OrderCode { get; init; }

    public string? OrderName { get; init; }

    public required string Code { get; init; }

    public string? CodeSystem { get; init; }

    public string? Display { get; init; }

    /// <summary>OBX-2, e.g. <c>NM</c>, <c>ST</c>, <c>CE</c>.</summary>
    public string? ValueType { get; init; }

    public string? Value { get; init; }

    public string? Units { get; init; }

    public string? ReferenceRange { get; init; }

    public string? AbnormalFlag { get; init; }

    /// <summary>OBX-11: <c>F</c> final, <c>P</c> preliminary, <c>C</c> corrected, ...</summary>
    public string? Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record OrganizationRef(string? Name, string? Id);

/// <summary>A billing or rendering provider. <see cref="Name"/> is an organization name or "Last, First".</summary>
public sealed record ProviderRecord(string? Name, string? Npi, string? TaxId, PostalAddress? Address);

/// <summary>A subscriber or patient on a claim.</summary>
public sealed record MemberRecord
{
    public string? MemberId { get; init; }

    public PersonName? Name { get; init; }

    public DateOnly? BirthDate { get; init; }

    public string? Sex { get; init; }

    public PostalAddress? Address { get; init; }

    /// <summary>Individual relationship code: SBR02 for the subscriber (<c>18</c> = self), PAT01 for a dependent.</summary>
    public string? RelationshipCode { get; init; }
}

public sealed record DiagnosisCode(string Qualifier, string Code);

public sealed record ServiceLineRecord
{
    public required int LineNumber { get; init; }

    /// <summary>SV101-1, usually <c>HC</c> (HCPCS/CPT).</summary>
    public required string ProcedureQualifier { get; init; }

    public required string ProcedureCode { get; init; }

    public IReadOnlyList<string> Modifiers { get; init; } = [];

    public required decimal Charge { get; init; }

    public string? UnitType { get; init; }

    public decimal? Units { get; init; }

    public string? PlaceOfService { get; init; }

    public IReadOnlyList<int> DiagnosisPointers { get; init; } = [];

    public DateOnly? ServiceDate { get; init; }

    public DateOnly? ServiceDateEnd { get; init; }
}

public sealed record ClaimRecord
{
    /// <summary>CLM01, the submitter's claim identifier.</summary>
    public required string ClaimId { get; init; }

    public required decimal TotalCharge { get; init; }

    public string? PlaceOfService { get; init; }

    /// <summary>CLM05-3: <c>1</c> original, <c>7</c> replacement, <c>8</c> void.</summary>
    public string? FrequencyCode { get; init; }

    public ProviderRecord? BillingProvider { get; init; }

    public ProviderRecord? RenderingProvider { get; init; }

    public MemberRecord? Subscriber { get; init; }

    /// <summary>Set when the patient is not the subscriber (loop 2000C).</summary>
    public MemberRecord? Patient { get; init; }

    public OrganizationRef? Payer { get; init; }

    public string? GroupNumber { get; init; }

    /// <summary>SBR01: <c>P</c> primary, <c>S</c> secondary, <c>T</c> tertiary, ...</summary>
    public string? PayerResponsibility { get; init; }

    /// <summary>SBR09, e.g. <c>CI</c> commercial, <c>MB</c> Medicare Part B, <c>MC</c> Medicaid.</summary>
    public string? ClaimFilingIndicator { get; init; }

    public IReadOnlyList<DiagnosisCode> Diagnoses { get; init; } = [];

    public IReadOnlyList<ServiceLineRecord> ServiceLines { get; init; } = [];
}
