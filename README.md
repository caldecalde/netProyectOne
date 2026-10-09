# CareFlow

**Healthcare data exchange platform on .NET 8 and AWS.** CareFlow ingests HL7 v2 messages and ASC X12 claims, stores them encrypted, parses them through an event-driven pipeline, and serves normalized records over REST and gRPC.

> All data in this repository is **synthetic**. No real patient information is used. See [ADR 0004](docs/adr/0004-synthetic-data-and-phi-handling.md).

```
 [Web UI / Postman] --JWT--> [Ingestion API] --raw file--> S3 careflow-raw (encrypted)
                                   |  file record + FileReceived in one MongoDB transaction (outbox)
                                   |  outbox relay --> SNS --> SQS parser-file-received (+DLQ), traceparent propagated
                                   v
                           [Parser Worker]  HL7 v2 (NHapi) / X12 837P (hand-written parser) + 999 ack
                                   |  normalized JSON + 999 --> S3 careflow-parsed (encrypted)
                                   |  MessageParsed / ParseFailed --> SNS --> SQS ingestion-parse-results (+DLQ)
                                   v
     [Ingestion API] status: Received -> Parsed | Failed          [Records Service] (Week 3) -- MongoDB -- REST + gRPC
```

## Status

| Phase | Scope | State |
|---|---|---|
| 1. Foundation | Ingestion API, auth, S3/SNS/MongoDB adapters, local stack, CI | ✅ Done |
| 2. Parsing | Parser worker, HL7 + X12 837P parsing, 999 ack, transactional outbox, idempotent consumers, DLQ handling | ✅ Done |
| 3. Records + gRPC | Records service, gRPC + JSON transcoding, 835 generation, Postman/Newman | ⏳ Next |
| 4. Cloud & Kubernetes | Lambda validator, Terraform, Helm, k3s on EC2, optional EKS | ⏳ |
| 5. Observability & performance | CloudWatch/X-Ray, dashboards, k6 load tests, BenchmarkDotNet | ⏳ |
| 6. Demo | Web UI, public demo, walkthrough | ⏳ |

## Quick start

Requirements: .NET SDK 8, Docker.

Build and start everything (LocalStack, MongoDB replica set, Keycloak, Jaeger, Ingestion API, Parser worker), then run the end-to-end check:

```powershell
docker compose -f deploy/docker-compose.yml up -d --build --wait
./scripts/smoke-test.ps1                                           # or: bash scripts/smoke-test.sh
```

Start again later without rebuilding, or stop:

```powershell
docker compose -f deploy/docker-compose.yml up -d --wait
docker compose -f deploy/docker-compose.yml stop
```

`docker compose -f deploy/docker-compose.yml down -v` resets everything, including MongoDB data.

| What | Where |
|---|---|
| Swagger UI | http://localhost:5080/swagger. Click **Authorize** and paste the output of `./scripts/get-token.ps1 submitter`. |
| Traces (Jaeger) | http://localhost:16686. One trace spans `ingestion-api` → `parser-worker` → `ingestion-api`. |
| Parser health | http://localhost:5081/health/ready (MongoDB, S3, input queue) |
| Keycloak admin | http://localhost:8080 (`admin` / `admin`) |
| Demo users | `submitter`, `viewer` and `auditor`. Each password is the same as the username. |

Upload a file and watch it move through the pipeline:

```bash
TOKEN=$(./scripts/get-token.sh submitter)
curl -H "Authorization: Bearer $TOKEN" -F "file=@samples/x12/837p-batch-partial.x12" http://localhost:5080/api/v1/files
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/v1/files/<id>          # "status": "Parsed"
docker compose -f deploy/docker-compose.yml exec localstack awslocal s3 ls s3://careflow-parsed --recursive
```

## API (Ingestion)

| Method | Route | Policy | Result |
|---|---|---|---|
| `POST` | `/api/v1/files` (multipart, `file`) | `submitter` | `202` + `Location` for new content. `200` returns the original record when identical content was already submitted. `400` (problem+json with `code`) for empty, too large or unsupported content. |
| `GET` | `/api/v1/files/{id}` | `submitter`, `viewer`, `auditor` | `status` is `Received`, then `Parsed` or `Failed` (with `failureReason`, e.g. `x12.interchange_invalid`) |
| `GET` | `/health/live`, `/health/ready` | anonymous | Liveness / readiness (MongoDB + S3) |

The format is detected from the **content**, never from the file name: an `MSH`, `FHS` or `BHS` header means HL7 v2, and a fixed-width `ISA` header with validated separators means X12. Duplicate submissions are detected by SHA-256 hash, backed by a unique index, so the check holds under concurrent uploads.

## Parsing

| Input | Library | Supported | Output |
|---|---|---|---|
| HL7 v2.x (read with the 2.5.1 model) | NHapi | ADT^A01/A04/A08 → patient + encounter; ORU^R01 → patient + observations. Batches (FHS/BHS), MLLP framing, CR/LF/CRLF. | Normalized JSON |
| ASC X12 005010X222A1 | Hand-written (`Parser.Domain/X12`) | 837P: envelopes, loops 1000A–2400, claims, subscribers, dependents, providers, diagnoses, service lines | Normalized JSON + **999** |

Every file produces a JSON document in `careflow-parsed/parsed/...` that holds the accepted messages and every issue found. X12 files also get a 999 in `careflow-parsed/acks/...`. One bad HL7 message or X12 transaction set rejects only itself. For example, [`837p-batch-partial.x12`](samples/x12/837p-batch-partial.x12) yields:

```
AK2*837*0001*005010X222A1~ IK5*A~
AK2*837*0002*005010X222A1~ IK3*SV1*16*2400*8~ IK4*2**6~ IK3*NM1*10*2010BB*3~ IK5*R*I5~
AK9*P*2*2*1~
```

The parser verifies the SHA-256 recorded at upload before parsing. Issues and 999s cite positions and codes, never field values (no IK404), so they are safe to store and log. Design notes: [ADR 0006](docs/adr/0006-hl7-with-nhapi-hand-written-x12.md).

## Reliability

- **Transactional outbox.** The file record and its `FileReceived` event commit in one MongoDB transaction. A background relay publishes the event and retries until SNS accepts it, so a broker outage cannot lose an upload's event. The relay restores the request's trace context, so traces stay connected.
- **Idempotent consumers.** An inbox of processed event IDs, deterministic output keys, result event IDs derived from the incoming event ID (UUID v5), and conditional status updates. Redelivered messages are harmless.
- **Retries and DLQs.** Polly retries in process, then SQS redelivers with exponential visibility backoff. After 5 receives the message goes to the queue's DLQ. Poison messages go to the DLQ at once, with a reason attribute. Unparseable content is a business outcome (`ParseFailed`), never a DLQ entry.
- **Operating the DLQs:**

  ```bash
  ./scripts/dlq.sh inspect parser-file-received   # depth, event IDs and dead-letter reasons (never bodies)
  ./scripts/dlq.sh redrive parser-file-received   # after a fix: move messages back; idempotency makes this safe
  ```

  The same commands exist as `dlq.ps1`. Add `CAREFLOW_AWS=aws` (or `-Aws`) to target real AWS.

Design notes: [ADR 0005](docs/adr/0005-transactional-outbox-and-reliable-consumers.md).

## Engineering highlights

- **Clean Architecture**, enforced by [architecture tests](tests/CareFlow.Architecture.Tests/LayeringTests.cs) for every service. Domains have no framework dependencies (the X12 parser is pure C#), NHapi, AWS and MongoDB stay in Infrastructure, adapters are `internal`, and services never reference each other.
- **Security:** JWT bearer with policy-based authorization. Role claims are normalized for both Keycloak and Amazon Cognito ([RoleClaimsTransformation](src/BuildingBlocks/CareFlow.BuildingBlocks/Security/RoleClaimsTransformation.cs)). Per-user upload rate limiting, request size limits, security headers, and RFC 9457 problem details that never echo content.
- **HIPAA-minded:** encryption at rest on every S3 write (raw and parsed buckets). Logs, events and parse issues never contain message content. Integrity is verified end to end by SHA-256, and the parser reads only from the trusted raw bucket.
- **Event-driven:** SNS → SQS fan-out with filter policies, a shared [SQS consumer](src/BuildingBlocks/CareFlow.BuildingBlocks.Aws/Sqs/SqsMessagePump.cs) (bounded concurrency, Polly, DLQ routing), and W3C `traceparent` carried in message attributes.
- **Observability:** OpenTelemetry traces and metrics exported over OTLP. Custom metrics cover files, parses, consumed messages by outcome, outbox publishes and outbox lag. Logs are structured JSON with trace IDs.
- **Testing:** xUnit + Moq unit tests (X12 tokenizer, envelope, 837P, 999 byte-for-byte, NHapi mapping, SQS pump DLQ routing). Integration tests run both services against real MongoDB + LocalStack via Testcontainers (outbox recovery, trace continuity, redelivery, poison → DLQ). An end-to-end smoke test in CI uses real Keycloak tokens.
- **Containers:** multi-stage builds into chiseled, non-root runtime images.
- **Cross-platform:** CI on Ubuntu and Windows, with scripts in both PowerShell and Bash.

## Repository layout

```
src/
  BuildingBlocks/   shared: Result<T>, event contracts, auth, observability; .Aws: SNS publisher, SQS consumer
  Ingestion/        Domain / Application / Infrastructure (outbox, relay) / Api
  Parser/           Domain (normalized model, X12 + 999) / Application / Infrastructure (NHapi, S3, inbox) / Worker
tests/              unit (xUnit+Moq), integration (Testcontainers), architecture (NetArchTest)
deploy/             docker-compose, LocalStack init, Keycloak realm
samples/            synthetic HL7 v2 and X12 files
scripts/            smoke tests, token helpers, DLQ inspect/redrive (.ps1 + .sh)
docs/adr/           architecture decision records
```

## Running tests

```bash
dotnet test                                                    # everything (integration tests need Docker)
dotnet test --filter "FullyQualifiedName!~IntegrationTests"   # no Docker needed
```
