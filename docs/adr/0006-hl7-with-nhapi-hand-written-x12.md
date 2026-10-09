# 0006 – HL7 v2 with NHapi, a hand-written X12 parser, and the 999 acknowledgment

**Status:** Accepted

## Context
The parser has to turn HL7 v2 messages (ADT, ORU) and X12 837P claims into one normalized model. The two standards differ in how much help the .NET ecosystem offers. HL7 v2 has a mature open-source model library. X12 has only commercial libraries or thin tokenizers. X12 trading partners also expect a 999 implementation acknowledgment for every functional group they send.

## Decision

**HL7 v2: NHapi** (`nHapi.Model.V251`), wrapped in an Infrastructure adapter (`NHapiHl7FileParser`).
- Every message is read with the 2.5.1 model, whatever MSH-12 says. Earlier 2.x versions are backward compatible for the fields mapped, and one model keeps the mapping in one place.
- Files may contain several messages, FHS/BHS batch envelopes, MLLP framing, and CR, LF or CRLF segment endings. Each message is parsed on its own, so one bad message rejects only itself.
- Supported: ADT^A01/A04/A08 (patient + encounter) and ORU^R01 (patient + observations). Required-field checks (MSH-10, PID-3, PID-5, PV1-2, OBX-3/11) run after NHapi's syntax parse.
- NHapi exception messages can quote field values, so only the segment, field position and error code are kept.

**X12: hand-written, in the Domain project, with no dependencies.**
- `X12Tokenizer` reads the delimiters from the fixed-width ISA. The standard does not fix them: the element separator is at offset 3, the repetition separator is ISA11, the component separator is ISA16, and the segment terminator follows it. The ISA is validated byte by byte before anything else is read. Tokenizing works on the raw bytes with spans.
- `X12EnvelopeReader` checks the control structure: trailers present, control numbers matching between header and trailer, and segment, transaction set and group counts.
- `Claim837PParser` is a loop-aware state machine for 005010X222A1. It covers loops 1000A/B, 2000A/2010AA, 2000B/2010BA/BB, 2000C/2010CA, 2300, 2310B and 2400, with required-segment, sequence, element and code checks. It maps claims, subscribers, dependents, payers, providers, diagnoses and service lines.
- `Ack999Builder` produces the 999 (005010X231A1): one FA group per received group, AK2/IK3/IK4/IK5 per transaction set, and AK9 with A/P/R. Sender and receiver are swapped, and the ack control number is derived from the ingestion ID, so reprocessing reproduces the same 999.

**Scope boundaries.**
- **Envelope errors** (bad ISA/IEA) reject the file with no 999, because that is TA1 territory and the groups inside cannot be trusted. Producing a TA1 is future work.
- **Business edits are warnings, not 999 errors.** Examples: a claim total that differs from its line charges, or an NPI with a bad check digit. Payers report those later, in the 277CA.
- **IK404 (copy of the bad data element) is never sent**, and parse issues never quote values. The 999 and the issue list are stored and forwarded, and must not carry PHI.

## Consequences
- The X12 code shows the format in detail: envelopes, loops, control totals and acknowledgments. It is fast and fully unit-tested against synthetic samples. It also has to be maintained, and it validates the rules that matter for 837P rather than the whole TR3. A commercial validator would still be the right call for certification-grade edits.
- Adding a transaction set (an 835 is generated in Week 3) reuses the tokenizer, the envelope reader and the 999 builder.
- NHapi pulls in a large model assembly, and it lives only in the parser's Infrastructure. Architecture tests keep it out of the Domain and Application layers.
