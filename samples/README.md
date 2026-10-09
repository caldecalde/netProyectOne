# Sample files

All data in this folder is **synthetic**. The names, identifiers and addresses are made up and do not describe real people. Never commit real PHI to this repository.

Files are stored byte-exact (`-text` in `.gitattributes`), because segment terminators matter.

| File | Standard | Content | Expected result |
|---|---|---|---|
| `hl7/adt-a01.hl7` | HL7 v2.5.1 | ADT^A01 admit: patient + inpatient visit. Segments are separated by CR, as the standard requires. | Parsed: 1 message |
| `hl7/oru-r01.hl7` | HL7 v2.5.1 | ORU^R01 lab result: metabolic panel with 3 observations, one flagged high. Timestamps carry a UTC offset. | Parsed: 1 message, 3 observations |
| `hl7/adt-batch.hl7` | HL7 v2.5.1 | FHS/BHS batch with an ADT^A08 update and an ADT^A04 registration, CRLF line endings. | Parsed: 2 messages |
| `x12/837p-single-claim.x12` | ASC X12 005010X222A1 | 837P professional claim: one billing provider, one subscriber, one service line. | Parsed; 999 `AK9*A` |
| `x12/837p-batch-partial.x12` | ASC X12 005010X222A1 | Two transaction sets. `0001`: two subscribers, one with a dependent (loop 2000C), three service lines. `0002`: payer loop 2010BB missing and a non-numeric line charge. | Parsed with 1 rejected; 999 `AK9*P*2*2*1` with IK3/IK4 detail |
