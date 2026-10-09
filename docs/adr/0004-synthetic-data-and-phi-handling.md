# 0004 – Synthetic data only; PHI-safe by default

**Status:** Accepted

## Context
CareFlow processes HL7 and X12 content, which in real use contains Protected Health Information (PHI) under HIPAA. This is a public portfolio project, so no real PHI may ever enter it. Even so, the code should behave as if it handles PHI, so the safeguards can be shown working.

## Decision
- **Synthetic data only.** Samples are hand-written or generated with Synthea, and are labeled as such in `samples/README.md`.
- **Encryption at rest:** every S3 write requests SSE-KMS (or SSE-S3 when no key is configured), and the bucket has default encryption and blocks public access.
- **Encryption in transit:** TLS terminates at the ingress in deployed environments.
- **Logs never contain message content.** Logs carry IDs, formats, sizes and error codes only. Client file names are sanitized and treated as display data.
- **Least privilege:** policy-based authorization (`submitter`, `viewer`, `auditor`) and one IAM role per service.
- **Integrity:** a SHA-256 of the raw content is stored and propagated in events. The parser verifies it before parsing and rejects content that does not match (`parser.integrity_mismatch`). It also reads only from the configured raw bucket, whatever an event says.
- **Events carry no PHI.** Integration events hold IDs, storage keys, counts and reason codes. Parsed documents and 999s live in a separate encrypted bucket (`careflow-parsed`), so IAM can grant the parser read-only access to raw files.
- **Parse errors never quote values.** HL7 issues name the segment and field (`PID-3`). X12 issues and 999s use positions and codes, and IK404 (copy of the bad element) is never sent.

## Planned
- A PHI-masking Serilog enricher, applied once parsed fields exist (Week 2).
- An append-only audit trail of read and write access (Week 3).
- `docs/HIPAA-safeguards.md`, which maps each safeguard to the HIPAA Security Rule section it addresses (Week 6).
