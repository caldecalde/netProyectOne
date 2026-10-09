# 0001 – Microservices with Clean Architecture per service

**Status:** Accepted

## Context
CareFlow takes in healthcare files, parses HL7 v2 and X12, and serves normalized records. These jobs differ in load and failure behavior. Uploads are I/O-bound and bursty. Parsing is CPU-bound and has to retry safely. Queries are read-heavy. A failure in one should not take down the others.

## Decision
- Three deployable services (**Ingestion**, **Parser**, **Records**) and one AWS Lambda (**Validator**). They talk through events, plus gRPC for synchronous queries.
- Each service is split into `Domain → Application → Infrastructure → Api/Worker` projects. Dependencies point inward only.
- Application code depends on *ports* (`IRawFileStore`, `IIngestionFileRepository`, `IEventPublisher`). AWS and MongoDB adapters live in Infrastructure and are `internal`.
- Expected failures (validation, not found) are returned as `Result<T>` values. Exceptions are kept for truly unexpected conditions.
- The rules are enforced by tests (`tests/CareFlow.Architecture.Tests`, NetArchTest), not just by convention.

## Consequences
- The domain and handlers are unit-tested without containers. Adapters are tested against real MongoDB and LocalStack via Testcontainers.
- There are more projects than a single-project API needs. Shared plumbing lives in `BuildingBlocks` to keep repetition down, and service-specific logic never goes there.
