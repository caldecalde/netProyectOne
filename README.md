# CareFlow

**Healthcare data exchange platform on .NET 8 and AWS.** CareFlow ingests HL7 v2 messages and ASC X12 claims, stores them encrypted, parses them through an event-driven pipeline, and serves normalized records over REST and gRPC.

> All data in this repository is **synthetic**. No real patient information is used. See [ADR 0004](docs/adr/0004-synthetic-data-and-phi-handling.md).

```
 [Web UI / Postman] --JWT--> [Ingestion API] --raw file--> S3 (encrypted)
                                   | FileReceived (SNS -> SQS, traceparent propagated)
                                   v
                           [Parser Worker]  HL7 v2 (NHapi) / X12 837 (custom parser)
                                   | MessageParsed / ParseFailed
                                   v
     [Records Service] -- MongoDB --  REST + gRPC
```

## Status

| Phase | Scope | State |
|---|---|---|
| 1. Foundation | Ingestion API, auth, S3/SNS/MongoDB adapters, local stack, CI | ✅ Done |
| 2. Parsing | Parser worker, HL7 + X12 837 parsing, 999 ack, outbox, DLQ handling | ⏳ Next |
| 3. Records + gRPC | Records service, gRPC + JSON transcoding, 835 generation, Postman/Newman | ⏳ |
| 4. Cloud & Kubernetes | Lambda validator, Terraform, Helm, k3s on EC2, optional EKS | ⏳ |
| 5. Observability & performance | CloudWatch/X-Ray, dashboards, k6 load tests, BenchmarkDotNet | ⏳ |
| 6. Demo | Web UI, public demo, walkthrough | ⏳ |

## Quick start

Requirements: .NET SDK 8, Docker.

```powershell
docker compose -f deploy/docker-compose.yml up -d --build --wait   # LocalStack, MongoDB, Keycloak, Jaeger, API
./scripts/smoke-test.ps1                                           # or: bash scripts/smoke-test.sh
```command to run the project terminal -aqui-
docker compose -f deploy/docker-compose.yml up -d --wait
```command tro stop the service
docker compose -f deploy/docker-compose.yml stop


| What | Where |
|---|---|
| Swagger UI | http://localhost:5080/swagger. Click **Authorize** and paste the output of `./scripts/get-token.ps1 submitter`. |
| Traces (Jaeger) | http://localhost:16686, service `ingestion-api` |
| Keycloak admin | http://localhost:8080 (`admin` / `admin`) |
| Demo users | `submitter`, `viewer` and `auditor`. Each password is the same as the username. |

Upload a file from the command line:

```bash
TOKEN=$(./scripts/get-token.sh submitter)
curl -H "Authorization: Bearer $TOKEN" -F "file=@samples/x12/837p-single-claim.x12" http://localhost:5080/api/v1/files
```

## API (Ingestion)

| Method | Route | Policy | Result |
|---|---|---|---|
| `POST` | `/api/v1/files` (multipart, `file`) | `submitter` | `202` + `Location` for new content. `200` returns the original record when identical content was already submitted. `400` (problem+json with `code`) for empty, too large or unsupported content. |
| `GET` | `/api/v1/files/{id}` | `submitter`, `viewer`, `auditor` | Processing status |
| `GET` | `/health/live`, `/health/ready` | anonymous | Liveness / readiness (MongoDB + S3) |

The format is detected from the **content**, never from the file name: an `MSH` header means HL7 v2, and a fixed-width `ISA` header with validated separators means X12. Duplicate submissions are detected by SHA-256 hash, backed by a unique index, so the check holds under concurrent uploads.

## Engineering highlights

- **Clean Architecture**, enforced by [architecture tests](tests/CareFlow.Architecture.Tests/LayeringTests.cs). The domain has no framework dependencies, and AWS/MongoDB adapters are `internal`.
- **Security:** JWT bearer with policy-based authorization. Role claims are normalized for both Keycloak and Amazon Cognito ([RoleClaimsTransformation](src/BuildingBlocks/CareFlow.BuildingBlocks/Security/RoleClaimsTransformation.cs)). Per-user upload rate limiting, request size limits, security headers, and RFC 9457 problem details that never echo content.
- **HIPAA-minded:** encryption at rest is set on every S3 write, logs never contain message content, file names are sanitized, and content hashes are carried end to end for integrity.
- **Event-driven:** SNS → SQS fan-out with filter policies and dead-letter queues. The W3C `traceparent` travels inside message attributes.
- **Observability:** OpenTelemetry traces and metrics (custom `careflow.files.*` metrics) exported over OTLP, plus structured JSON logs that carry trace IDs.
- **Testing:** xUnit + Moq unit tests, integration tests against real MongoDB + LocalStack via Testcontainers, and an end-to-end smoke test in CI with real Keycloak tokens.
- **Containers:** multi-stage build into a chiseled, non-root runtime image.
- **Cross-platform:** CI on Ubuntu and Windows, with scripts in both PowerShell and Bash.

## Repository layout

```
src/
  BuildingBlocks/   shared: Result<T>, event contracts, auth, observability
  Ingestion/        Domain / Application / Infrastructure / Api
tests/              unit (xUnit+Moq), integration (Testcontainers), architecture (NetArchTest)
deploy/             docker-compose, LocalStack init, Keycloak realm
samples/            synthetic HL7 v2 and X12 files
scripts/            smoke tests and token helpers (.ps1 + .sh)
docs/adr/           architecture decision records
```

## Running tests

```bash
dotnet test                                              # everything (integration tests need Docker)
dotnet test --filter "FullyQualifiedName!~IntegrationTests"   # no Docker needed
```
