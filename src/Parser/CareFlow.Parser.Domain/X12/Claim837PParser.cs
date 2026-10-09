using System.Globalization;

namespace CareFlow.Parser.Domain.X12;

/// <summary>
/// Maps an 837P professional claim (implementation guide 005010X222A1) onto <see cref="ClaimRecord"/>s and checks the
/// implementation rules a 999 reports on: required loops and segments, segment order, required elements and code values.
/// Errors are written to the transaction set (they drive its 999 IK3/IK4/IK5); business edits such as claim balancing
/// are returned as warnings, because a payer reports those later (277CA), not in the 999.
/// </summary>
/// <remarks>
/// Loops handled: 1000A submitter, 1000B receiver, 2000A/2010AA billing provider, 2000B/2010BA/2010BB subscriber and
/// payer, 2000C/2010CA patient, 2300 claim, 2310B rendering provider, 2400 service line. Other valid 837P segments are
/// accepted and skipped.
/// </remarks>
public sealed class Claim837PParser
{
    public const string ImplementationGuide = "005010X222A1";

    /// <summary>Segments that may appear in an 837P. Anything else is an unexpected segment (IK304 = 2).</summary>
    private static readonly HashSet<string> Known837PSegments =
    [
        "BHT", "NM1", "PER", "HL", "PRV", "CUR", "N3", "N4", "REF", "SBR", "PAT", "DMG", "CLM", "DTP", "PWK", "CN1",
        "AMT", "K3", "NTE", "CR1", "CR2", "CRC", "HI", "HCP", "LX", "SV1", "SV5", "CR3", "QTY", "MEA", "PS1", "LIN",
        "CTP", "SVD", "CAS", "LQ", "FRM", "MOA", "OI", "MIA",
    ];

    private static readonly HashSet<string> PayerResponsibilityCodes = ["A", "B", "C", "D", "E", "F", "G", "H", "P", "S", "T", "U"];

    private readonly X12TransactionSet _set;
    private readonly List<ParseIssue> _warnings = [];
    private readonly List<ClaimBuilder> _claims = [];
    private readonly List<ProviderBuilder> _billingProviders = [];
    private readonly List<SubscriberBuilder> _subscribers = [];

    private string _loop = "HEADER";
    private Entity _entity = Entity.None;
    private bool _sawBht;
    private string? _submitter;
    private string? _receiver;
    private ProviderBuilder? _billing;
    private SubscriberBuilder? _subscriber;
    private PatientBuilder? _patient;
    private ClaimBuilder? _claim;
    private LineBuilder? _line;

    private Claim837PParser(X12TransactionSet set) => _set = set;

    private enum Entity
    {
        None,
        Other,
        BillingProvider,
        Subscriber,
        Payer,
        Patient,
        RenderingProvider,
    }

    /// <summary>
    /// Parses one transaction set. Returns the normalized message when the set is accepted (no errors), otherwise
    /// <c>null</c>; in both cases warnings are returned and errors are recorded on <paramref name="set"/>.
    /// </summary>
    public static (ParsedMessage? Message, IReadOnlyList<ParseIssue> Warnings) Parse(X12TransactionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var parser = new Claim837PParser(set);
        parser.Run();
        if (set.SegmentErrors.Count > 0 && !set.ErrorCodes.Contains(TransactionSetErrorCodes.ImplementationSegmentsInError))
        {
            set.ErrorCodes.Add(TransactionSetErrorCodes.ImplementationSegmentsInError);
        }

        return (set.IsAccepted ? parser.BuildMessage() : null, parser._warnings);
    }

    private void Run()
    {
        var first = true;
        foreach (var segment in _set.Body)
        {
            if (first && segment.Id != "BHT")
            {
                SegmentError(segment, "BHT", SegmentErrorCodes.RequiredSegmentMissing);
            }

            first = false;
            var errors = new List<X12ElementError>();
            Handle(segment, errors);
            if (errors.Count > 0)
            {
                _set.SegmentErrors.Add(new X12SegmentError(segment.Id, _set.PositionOf(segment), _loop, SegmentErrorCodes.SegmentHasDataElementErrors, errors));
            }
        }

        CloseClaim();
        CheckRequiredStructure();
    }

    private void Handle(X12Segment s, List<X12ElementError> errors)
    {
        switch (s.Id)
        {
            case "BHT":
                HandleBht(s, errors);
                break;
            case "NM1":
                HandleNm1(s, errors);
                break;
            case "HL":
                HandleHl(s, errors);
                break;
            case "N3":
                Address(s)?.SetLines(s[1], s[2]);
                break;
            case "N4":
                Address(s)?.SetCity(s[1], s[2], s[3], s[4]);
                break;
            case "REF":
                // Only the billing provider's EIN is kept. REF*SY (SSN) and other identifiers are deliberately dropped.
                if (_entity == Entity.BillingProvider && s[1] == "EI" && _billing is not null)
                {
                    _billing.TaxId = s[2];
                }

                break;
            case "SBR":
                HandleSbr(s, errors);
                break;
            case "PAT":
                HandlePat(s);
                break;
            case "DMG":
                HandleDmg(s, errors);
                break;
            case "CLM":
                HandleClm(s, errors);
                break;
            case "HI":
                HandleHi(s, errors);
                break;
            case "LX":
                HandleLx(s, errors);
                break;
            case "SV1":
                HandleSv1(s, errors);
                break;
            case "DTP":
                HandleDtp(s, errors);
                break;
            default:
                if (!Known837PSegments.Contains(s.Id))
                {
                    var code = IsValidSegmentId(s.Id) ? SegmentErrorCodes.UnexpectedSegment : SegmentErrorCodes.UnrecognizedSegmentId;
                    SegmentError(s, s.Id, code);
                }

                break;
        }
    }

    private void HandleBht(X12Segment s, List<X12ElementError> errors)
    {
        if (_sawBht)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.UnexpectedSegment);
            return;
        }

        _sawBht = true;
        RequireCode(s, 1, errors, "0019");
        RequireCode(s, 2, errors, "00", "18");
        Require(s, 3, errors);
        RequireDate(s, 4, errors);
        RequireCode(s, 6, errors, "CH", "RP", "31");
    }

    private void HandleNm1(X12Segment s, List<X12ElementError> errors)
    {
        // Until the loop is known to be valid here, N3/N4/DMG that follow must not attach to the previous party.
        _entity = Entity.Other;
        Require(s, 1, errors);
        RequireCode(s, 2, errors, "1", "2");
        switch (s[1])
        {
            case "41":
                _loop = "1000A";
                _entity = Entity.Other;
                Require(s, 3, errors);
                _submitter = s[3];
                break;
            case "40":
                _loop = "1000B";
                _entity = Entity.Other;
                Require(s, 3, errors);
                _receiver = s[3];
                break;
            case "85":
                _loop = "2010AA";
                if (!InLevel(s, "20", "2010AA") || _billing is null)
                {
                    return;
                }

                _entity = Entity.BillingProvider;
                RequireCode(s, 8, errors, "XX");
                Require(s, 9, errors);
                _billing.HasName = true;
                _billing.Name = ProviderName(s);
                _billing.Npi = s[9];
                CheckNpi(s);
                break;
            case "IL":
                _loop = "2010BA";
                if (!InLevel(s, "22", "2010BA") || _subscriber is null)
                {
                    return;
                }

                _entity = Entity.Subscriber;
                RequireCode(s, 8, errors, "MI", "II");
                Require(s, 9, errors);
                _subscriber.HasMember = true;
                _subscriber.Member.MemberId = s[9];
                _subscriber.Member.Name = new PersonName(Blank(s[3]), Blank(s[4]), Blank(s[5]));
                break;
            case "PR":
                _loop = "2010BB";
                if (!InLevel(s, "22", "2010BB") || _subscriber is null)
                {
                    return;
                }

                _entity = Entity.Payer;
                Require(s, 3, errors);
                RequireCode(s, 8, errors, "PI", "XV");
                Require(s, 9, errors);
                _subscriber.HasPayer = true;
                _subscriber.Payer = new OrganizationRef(Blank(s[3]), Blank(s[9]));
                break;
            case "QC":
                _loop = "2010CA";
                if (!InLevel(s, "23", "2010CA") || _patient is null)
                {
                    return;
                }

                _entity = Entity.Patient;
                _patient.HasName = true;
                _patient.Member.Name = new PersonName(Blank(s[3]), Blank(s[4]), Blank(s[5]));
                break;
            case "82":
                _loop = "2310B";
                if (_claim is null || _line is not null)
                {
                    // 2420A (line-level rendering provider) is valid but not mapped.
                    _entity = Entity.Other;
                    return;
                }

                _entity = Entity.RenderingProvider;
                _claim.Rendering = new ProviderBuilder(s) { Name = ProviderName(s), Npi = Blank(s[9]), HasName = true };
                CheckNpi(s);
                break;
            default:
                // Other 837P name loops (pay-to, referring, service facility, ...) are valid and skipped.
                _entity = Entity.Other;
                break;
        }
    }

    private void HandleHl(X12Segment s, List<X12ElementError> errors)
    {
        CloseClaim();
        _entity = Entity.None;
        Require(s, 1, errors);
        switch (s[3])
        {
            case "20":
                _loop = "2000A";
                _billing = new ProviderBuilder(s);
                _billingProviders.Add(_billing);
                _subscriber = null;
                _patient = null;
                break;
            case "22":
                _loop = "2000B";
                if (_billing is null)
                {
                    SegmentError(s, "HL", SegmentErrorCodes.NotInProperSequence);
                    return;
                }

                _subscriber = new SubscriberBuilder(s, _billing);
                _subscribers.Add(_subscriber);
                _patient = null;
                break;
            case "23":
                _loop = "2000C";
                if (_subscriber is null)
                {
                    SegmentError(s, "HL", SegmentErrorCodes.NotInProperSequence);
                    return;
                }

                _patient = new PatientBuilder(s);
                _subscriber.Patients.Add(_patient);
                break;
            default:
                errors.Add(new X12ElementError("3", ElementErrorCodes.InvalidCodeValue));
                break;
        }
    }

    private void HandleSbr(X12Segment s, List<X12ElementError> errors)
    {
        if (!InLevel(s, "22", "2000B") || _subscriber is null)
        {
            return;
        }

        _subscriber.HasSbr = true;
        RequireCode(s, 1, errors, [.. PayerResponsibilityCodes]);
        Require(s, 9, errors);
        _subscriber.PayerResponsibility = Blank(s[1]);
        _subscriber.Member.RelationshipCode = Blank(s[2]);
        _subscriber.GroupNumber = Blank(s[3]);
        _subscriber.ClaimFilingIndicator = Blank(s[9]);
    }

    private void HandlePat(X12Segment s)
    {
        if (!InLevel(s, "23", "2000C") || _patient is null)
        {
            return;
        }

        _patient.HasPat = true;
        _patient.Member.RelationshipCode = Blank(s[1]);
    }

    private void HandleDmg(X12Segment s, List<X12ElementError> errors)
    {
        var member = _entity switch
        {
            Entity.Subscriber => _subscriber?.Member,
            Entity.Patient => _patient?.Member,
            _ => null,
        };
        if (member is null)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.NotInProperSequence);
            return;
        }

        RequireCode(s, 1, errors, "D8");
        member.BirthDate = RequireDate(s, 2, errors);
        RequireCode(s, 3, errors, "F", "M", "U");
        member.Sex = Blank(s[3]);
    }

    private void HandleClm(X12Segment s, List<X12ElementError> errors)
    {
        CloseClaim();
        _loop = "2300";
        _entity = Entity.None;
        if (_subscriber is null)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.NotInProperSequence);
            return;
        }

        Require(s, 1, errors);
        if (s[1].Length > 38)
        {
            errors.Add(new X12ElementError("1", ElementErrorCodes.TooLong));
        }

        var total = RequireAmount(s, 2, errors);
        if (s.Component(5, 1).Length == 0)
        {
            errors.Add(new X12ElementError("5:1", ElementErrorCodes.RequiredMissing));
        }

        if (s.Component(5, 2) != "B")
        {
            errors.Add(new X12ElementError("5:2", ElementErrorCodes.InvalidCodeValue));
        }

        if (s.Component(5, 3).Length == 0)
        {
            errors.Add(new X12ElementError("5:3", ElementErrorCodes.RequiredMissing));
        }

        _claim = new ClaimBuilder(s, _subscriber, _patient)
        {
            ClaimId = s[1],
            TotalCharge = total ?? 0m,
            PlaceOfService = Blank(s.Component(5, 1)),
            FrequencyCode = Blank(s.Component(5, 3)),
        };
    }

    private void HandleHi(X12Segment s, List<X12ElementError> errors)
    {
        if (_claim is null || _line is not null)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.NotInProperSequence);
            return;
        }

        _claim.HasDiagnosis = true;
        for (var i = 1; i <= 12 && s.Has(i); i++)
        {
            var qualifier = s.Component(i, 1);
            var code = s.Component(i, 2);
            var valid = i == 1 ? qualifier is "ABK" or "BK" : qualifier is "ABF" or "BF";
            if (!valid)
            {
                errors.Add(new X12ElementError($"{i}:1", ElementErrorCodes.InvalidCodeValue));
            }

            if (code.Length == 0)
            {
                errors.Add(new X12ElementError($"{i}:2", ElementErrorCodes.RequiredMissing));
                continue;
            }

            _claim.Diagnoses.Add(new DiagnosisCode(qualifier, code));
        }
    }

    private void HandleLx(X12Segment s, List<X12ElementError> errors)
    {
        _loop = "2400";
        _entity = Entity.None;
        if (_claim is null)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.NotInProperSequence);
            return;
        }

        if (!int.TryParse(s[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            errors.Add(new X12ElementError("1", s.Has(1) ? ElementErrorCodes.InvalidCharacter : ElementErrorCodes.RequiredMissing));
        }

        _line = new LineBuilder(s) { LineNumber = number };
        _claim.Lines.Add(_line);
    }

    private void HandleSv1(X12Segment s, List<X12ElementError> errors)
    {
        if (_line is null || _line.HasService)
        {
            SegmentError(s, s.Id, _line is null ? SegmentErrorCodes.NotInProperSequence : SegmentErrorCodes.UnexpectedSegment);
            return;
        }

        _line.HasService = true;
        var qualifier = s.Component(1, 1);
        if (qualifier is not ("HC" or "ER" or "IV" or "WK"))
        {
            errors.Add(new X12ElementError("1:1", qualifier.Length == 0 ? ElementErrorCodes.RequiredMissing : ElementErrorCodes.InvalidCodeValue));
        }

        if (s.Component(1, 2).Length == 0)
        {
            errors.Add(new X12ElementError("1:2", ElementErrorCodes.RequiredMissing));
        }

        _line.ProcedureQualifier = qualifier;
        _line.ProcedureCode = s.Component(1, 2);
        _line.Modifiers = s.Components(1).Skip(2).Take(4).Where(m => m.Length > 0).ToArray();
        _line.Charge = RequireAmount(s, 2, errors) ?? 0m;
        RequireCode(s, 3, errors, "UN", "MJ");
        _line.UnitType = Blank(s[3]);
        _line.Units = RequireAmount(s, 4, errors);
        _line.PlaceOfService = Blank(s[5]);

        var pointers = new List<int>();
        foreach (var (pointer, index) in s.Components(7).Select((p, i) => (p, i + 1)).Where(p => p.p.Length > 0))
        {
            if (int.TryParse(pointer, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 12)
            {
                pointers.Add(value);
            }
            else
            {
                errors.Add(new X12ElementError($"7:{index}", ElementErrorCodes.InvalidCodeValue));
            }
        }

        if (pointers.Count == 0)
        {
            errors.Add(new X12ElementError("7:1", ElementErrorCodes.RequiredMissing));
        }

        _line.DiagnosisPointers = pointers;
    }

    private void HandleDtp(X12Segment s, List<X12ElementError> errors)
    {
        Require(s, 1, errors);
        RequireCode(s, 2, errors, "D8", "RD8");
        var (from, to) = ParseDatePeriod(s[2], s[3]);
        if (from is null)
        {
            errors.Add(new X12ElementError("3", s.Has(3) ? ElementErrorCodes.InvalidDate : ElementErrorCodes.RequiredMissing));
            return;
        }

        if (s[1] != "472")
        {
            return;
        }

        if (_line is not null)
        {
            _line.ServiceDate = from;
            _line.ServiceDateEnd = to;
        }
        else if (_claim is not null)
        {
            _claim.ServiceDate = from;
            _claim.ServiceDateEnd = to;
        }
    }

    private void CloseClaim()
    {
        if (_claim is not null)
        {
            _claims.Add(_claim);
        }

        _claim = null;
        _line = null;
    }

    private void CheckRequiredStructure()
    {
        var end = _set.Se ?? _set.Segments[^1];
        var firstHl = _set.Body.FirstOrDefault(s => s.Id == "HL") ?? end;

        // A missing BHT is reported at the first body segment by Run(); an empty body has none.
        if (!_set.Body.Any())
        {
            SegmentError(end, "BHT", SegmentErrorCodes.RequiredSegmentMissing);
        }

        if (_submitter is null)
        {
            SegmentError(firstHl, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "1000A");
        }

        if (_receiver is null)
        {
            SegmentError(firstHl, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "1000B");
        }

        if (_billingProviders.Count == 0)
        {
            SegmentError(end, "HL", SegmentErrorCodes.RequiredSegmentMissing, "2000A");
        }

        foreach (var billing in _billingProviders.Where(b => !b.HasName))
        {
            SegmentError(billing.Start, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "2010AA");
        }

        foreach (var subscriber in _subscribers)
        {
            if (!subscriber.HasSbr)
            {
                SegmentError(subscriber.Start, "SBR", SegmentErrorCodes.RequiredSegmentMissing, "2000B");
            }

            if (!subscriber.HasMember)
            {
                SegmentError(subscriber.Start, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "2010BA");
            }

            if (!subscriber.HasPayer)
            {
                SegmentError(subscriber.Start, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "2010BB");
            }

            foreach (var patient in subscriber.Patients)
            {
                if (!patient.HasPat)
                {
                    SegmentError(patient.Start, "PAT", SegmentErrorCodes.RequiredSegmentMissing, "2000C");
                }

                if (!patient.HasName)
                {
                    SegmentError(patient.Start, "NM1", SegmentErrorCodes.RequiredSegmentMissing, "2010CA");
                }
            }
        }

        if (_claims.Count == 0)
        {
            SegmentError(end, "CLM", SegmentErrorCodes.RequiredSegmentMissing, "2300");
        }

        foreach (var claim in _claims)
        {
            if (!claim.HasDiagnosis)
            {
                SegmentError(claim.Start, "HI", SegmentErrorCodes.RequiredSegmentMissing, "2300");
            }

            if (claim.Lines.Count == 0)
            {
                SegmentError(claim.Start, "LX", SegmentErrorCodes.RequiredSegmentMissing, "2400");
            }

            foreach (var line in claim.Lines)
            {
                if (!line.HasService)
                {
                    SegmentError(line.Start, "SV1", SegmentErrorCodes.RequiredSegmentMissing, "2400");
                }

                if (line.ServiceDate is null && claim.ServiceDate is null)
                {
                    SegmentError(line.Start, "DTP", SegmentErrorCodes.RequiredSegmentMissing, "2400");
                }
            }

            var lineTotal = claim.Lines.Sum(l => l.Charge);
            if (claim.Lines.Count > 0 && lineTotal != claim.TotalCharge)
            {
                _warnings.Add(Warning("x12.claim_unbalanced", claim.Start, "CLM02 does not equal the sum of the service line charges (SV102)."));
            }
        }
    }

    private ParsedMessage BuildMessage()
    {
        var bht = _set.Body.FirstOrDefault(s => s.Id == "BHT");
        return new ParsedMessage
        {
            MessageType = "837P",
            ControlId = _set.ControlNumber,
            Version = _set.ImplementationReference,
            CreatedAt = bht is null ? null : ParseDateTime(bht[4], bht[5]),
            Sender = _submitter,
            Receiver = _receiver,
            Claims = _claims.Select(c => c.Build()).ToArray(),
        };
    }

    private bool InLevel(X12Segment s, string level, string loop)
    {
        var current = level switch
        {
            "20" => _billing is not null && _subscriber is null,
            "22" => _subscriber is not null && _patient is null && _claim is null,
            "23" => _patient is not null && _claim is null,
            _ => false,
        };
        if (!current)
        {
            _set.SegmentErrors.Add(new X12SegmentError(s.Id, _set.PositionOf(s), loop, SegmentErrorCodes.NotInProperSequence, []));
        }

        return current;
    }

    private AddressBuilder? Address(X12Segment s)
    {
        var target = _entity switch
        {
            Entity.BillingProvider => _billing?.Address,
            Entity.Subscriber => _subscriber?.Member.Address,
            Entity.Patient => _patient?.Member.Address,
            Entity.RenderingProvider => _claim?.Rendering?.Address,
            Entity.Other or Entity.Payer => null,
            _ => null,
        };
        if (target is null && _entity == Entity.None)
        {
            SegmentError(s, s.Id, SegmentErrorCodes.NotInProperSequence);
        }

        return target;
    }

    private void CheckNpi(X12Segment nm1)
    {
        if (nm1[8] == "XX" && nm1.Has(9) && !IsValidNpi(nm1[9]))
        {
            _warnings.Add(Warning("x12.npi_check_digit", nm1, "NM109 is not a valid NPI (check digit)."));
        }
    }

    private void SegmentError(X12Segment at, string segmentId, string code, string? loop = null) =>
        _set.SegmentErrors.Add(new X12SegmentError(segmentId, _set.PositionOf(at), loop ?? _loop, code, []));

    private ParseIssue Warning(string code, X12Segment at, string description) =>
        new(IssueSeverity.Warning, code, $"ST {_set.ControlNumber} / {at.Id} (position {_set.PositionOf(at)})", description) { ControlId = _set.ControlNumber };

    private static bool Require(X12Segment s, int index, List<X12ElementError> errors)
    {
        if (s.Has(index))
        {
            return true;
        }

        errors.Add(new X12ElementError(index.ToString(CultureInfo.InvariantCulture), ElementErrorCodes.RequiredMissing));
        return false;
    }

    private static void RequireCode(X12Segment s, int index, List<X12ElementError> errors, params string[] allowed)
    {
        if (Require(s, index, errors) && !allowed.Contains(s[index], StringComparer.Ordinal))
        {
            errors.Add(new X12ElementError(index.ToString(CultureInfo.InvariantCulture), ElementErrorCodes.InvalidCodeValue));
        }
    }

    private static decimal? RequireAmount(X12Segment s, int index, List<X12ElementError> errors)
    {
        if (!Require(s, index, errors))
        {
            return null;
        }

        if (decimal.TryParse(s[index], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add(new X12ElementError(index.ToString(CultureInfo.InvariantCulture), ElementErrorCodes.InvalidCharacter));
        return null;
    }

    private static DateOnly? RequireDate(X12Segment s, int index, List<X12ElementError> errors)
    {
        if (!Require(s, index, errors))
        {
            return null;
        }

        if (TryParseDate(s[index], out var date))
        {
            return date;
        }

        errors.Add(new X12ElementError(index.ToString(CultureInfo.InvariantCulture), ElementErrorCodes.InvalidDate));
        return null;
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static (DateOnly? From, DateOnly? To) ParseDatePeriod(string format, string value)
    {
        if (format == "RD8")
        {
            var parts = value.Split('-');
            return parts.Length == 2 && TryParseDate(parts[0], out var from) && TryParseDate(parts[1], out var to) && to >= from
                ? (from, to)
                : (null, null);
        }

        return TryParseDate(value, out var date) ? (date, null) : (null, null);
    }

    /// <summary>X12 dates and times carry no time zone; they are kept as sent and labeled UTC.</summary>
    private static DateTimeOffset? ParseDateTime(string date, string time)
    {
        if (!TryParseDate(date, out var day))
        {
            return null;
        }

        var parsedTime = TimeOnly.TryParseExact(time, ["HHmm", "HHmmss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : TimeOnly.MinValue;
        return new DateTimeOffset(day.ToDateTime(parsedTime), TimeSpan.Zero);
    }

    private static string ProviderName(X12Segment nm1) =>
        nm1[2] == "1" && nm1.Has(4) ? $"{nm1[3]}, {nm1[4]}" : nm1[3];

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static bool IsValidSegmentId(string id) =>
        id.Length is 2 or 3 && id.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) && char.IsAsciiLetterUpper(id[0]);

    /// <summary>NPI check digit: Luhn over the 9 base digits prefixed with the card issuer prefix 80840.</summary>
    internal static bool IsValidNpi(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsAsciiDigit))
        {
            return false;
        }

        var payload = "80840" + npi[..9];
        var sum = 0;
        var doubleIt = true;
        for (var i = payload.Length - 1; i >= 0; i--)
        {
            var digit = payload[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return (10 - (sum % 10)) % 10 == npi[9] - '0';
    }

    private sealed class AddressBuilder
    {
        private string? _line1;
        private string? _line2;
        private string? _city;
        private string? _state;
        private string? _postalCode;
        private string? _country;

        public void SetLines(string line1, string line2)
        {
            _line1 = Blank(line1);
            _line2 = Blank(line2);
        }

        public void SetCity(string city, string state, string postalCode, string country)
        {
            _city = Blank(city);
            _state = Blank(state);
            _postalCode = Blank(postalCode);
            _country = Blank(country);
        }

        public PostalAddress? Build() =>
            _line1 is null && _city is null ? null : new PostalAddress(_line1, _line2, _city, _state, _postalCode, _country);
    }

    private sealed class ProviderBuilder(X12Segment start)
    {
        public X12Segment Start { get; } = start;

        public bool HasName { get; set; }

        public string? Name { get; set; }

        public string? Npi { get; set; }

        public string? TaxId { get; set; }

        public AddressBuilder Address { get; } = new();

        public ProviderRecord Build() => new(Name, Npi, TaxId, Address.Build());
    }

    private sealed class MemberBuilder
    {
        public string? MemberId { get; set; }

        public PersonName? Name { get; set; }

        public DateOnly? BirthDate { get; set; }

        public string? Sex { get; set; }

        public string? RelationshipCode { get; set; }

        public AddressBuilder Address { get; } = new();

        public MemberRecord Build() => new()
        {
            MemberId = MemberId,
            Name = Name,
            BirthDate = BirthDate,
            Sex = Sex,
            Address = Address.Build(),
            RelationshipCode = RelationshipCode,
        };
    }

    private sealed class SubscriberBuilder(X12Segment start, ProviderBuilder billing)
    {
        public X12Segment Start { get; } = start;

        public ProviderBuilder Billing { get; } = billing;

        public bool HasSbr { get; set; }

        public bool HasMember { get; set; }

        public bool HasPayer { get; set; }

        public MemberBuilder Member { get; } = new();

        public OrganizationRef? Payer { get; set; }

        public string? GroupNumber { get; set; }

        public string? PayerResponsibility { get; set; }

        public string? ClaimFilingIndicator { get; set; }

        public List<PatientBuilder> Patients { get; } = [];
    }

    private sealed class PatientBuilder(X12Segment start)
    {
        public X12Segment Start { get; } = start;

        public bool HasPat { get; set; }

        public bool HasName { get; set; }

        public MemberBuilder Member { get; } = new();
    }

    private sealed class LineBuilder(X12Segment start)
    {
        public X12Segment Start { get; } = start;

        public int LineNumber { get; init; }

        public bool HasService { get; set; }

        public string ProcedureQualifier { get; set; } = string.Empty;

        public string ProcedureCode { get; set; } = string.Empty;

        public IReadOnlyList<string> Modifiers { get; set; } = [];

        public decimal Charge { get; set; }

        public string? UnitType { get; set; }

        public decimal? Units { get; set; }

        public string? PlaceOfService { get; set; }

        public IReadOnlyList<int> DiagnosisPointers { get; set; } = [];

        public DateOnly? ServiceDate { get; set; }

        public DateOnly? ServiceDateEnd { get; set; }
    }

    private sealed class ClaimBuilder(X12Segment start, SubscriberBuilder subscriber, PatientBuilder? patient)
    {
        public X12Segment Start { get; } = start;

        public required string ClaimId { get; init; }

        public decimal TotalCharge { get; init; }

        public string? PlaceOfService { get; init; }

        public string? FrequencyCode { get; init; }

        public bool HasDiagnosis { get; set; }

        public List<DiagnosisCode> Diagnoses { get; } = [];

        public List<LineBuilder> Lines { get; } = [];

        public ProviderBuilder? Rendering { get; set; }

        public DateOnly? ServiceDate { get; set; }

        public DateOnly? ServiceDateEnd { get; set; }

        public ClaimRecord Build() => new()
        {
            ClaimId = ClaimId,
            TotalCharge = TotalCharge,
            PlaceOfService = PlaceOfService,
            FrequencyCode = FrequencyCode,
            BillingProvider = subscriber.Billing.Build(),
            RenderingProvider = Rendering?.Build(),
            Subscriber = subscriber.Member.Build(),
            Patient = patient?.Member.Build(),
            Payer = subscriber.Payer,
            GroupNumber = subscriber.GroupNumber,
            PayerResponsibility = subscriber.PayerResponsibility,
            ClaimFilingIndicator = subscriber.ClaimFilingIndicator,
            Diagnoses = Diagnoses.ToArray(),
            ServiceLines = Lines.Select(l => new ServiceLineRecord
            {
                LineNumber = l.LineNumber,
                ProcedureQualifier = l.ProcedureQualifier,
                ProcedureCode = l.ProcedureCode,
                Modifiers = l.Modifiers,
                Charge = l.Charge,
                UnitType = l.UnitType,
                Units = l.Units,
                PlaceOfService = l.PlaceOfService,
                DiagnosisPointers = l.DiagnosisPointers,
                ServiceDate = l.ServiceDate ?? ServiceDate,
                ServiceDateEnd = l.ServiceDate is null ? ServiceDateEnd : l.ServiceDateEnd,
            }).ToArray(),
        };
    }
}
