# Architecture Decision Records

Short records of decisions that shape the codebase. Each has a status, the context and forces, the decision, and its consequences.

| # | Decision | Status |
|---|---|---|
| [0001](0001-clean-architecture-microservices.md) | Microservices with Clean Architecture per service | Accepted |
| [0002](0002-target-dotnet-8.md) | Target .NET 8 (LTS) with a planned move to .NET 10 | Accepted |
| [0003](0003-sns-sqs-event-backbone.md) | SNS → SQS fan-out as the event backbone | Accepted |
| [0004](0004-synthetic-data-and-phi-handling.md) | Synthetic data only; PHI-safe by default | Accepted |
| [0005](0005-transactional-outbox-and-reliable-consumers.md) | Transactional outbox, idempotent consumers, dead-letter handling | Accepted |
| [0006](0006-hl7-with-nhapi-hand-written-x12.md) | HL7 v2 with NHapi; hand-written X12 parser and 999 acknowledgment | Accepted |
