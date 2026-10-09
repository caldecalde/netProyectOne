# 0003 – SNS → SQS fan-out as the event backbone

**Status:** Accepted

## Context
Services must react to each other's events without the producer knowing who consumes them. Messages must survive consumer outages, and poison messages must not block a queue.

## Decision
- All integration events go to **one SNS topic** (`careflow-events`) with an `event-type` message attribute.
- Each consumer owns an **SQS queue per event type**. It subscribes with a **filter policy** and **raw message delivery**, and has its own **dead-letter queue** (`maxReceiveCount = 5`).
- Contracts live in `BuildingBlocks/Messaging`. Breaking changes introduce a new type (`FileReceivedV2`) instead of editing the existing one.
- The OpenTelemetry AWS instrumentation injects `traceparent` into message attributes, so one trace spans HTTP → S3 → SNS → SQS → consumer.
- Consumers must be **idempotent**: delivery is at-least-once. Every event carries an `EventId` for de-duplication.

## Delivery guarantees
In Week 1, Ingestion wrote MongoDB and then published to SNS, so an SNS outage between the two steps could leave a stored file without its event. That gap is closed by a **transactional outbox**, and consumers are idempotent and dead-letter what they cannot process. See [ADR 0005](0005-transactional-outbox-and-reliable-consumers.md).

## Alternatives considered
- **EventBridge:** richer routing, but more than this system needs, and weaker LocalStack parity.
- **Kafka / MSK:** high throughput and replay, but it adds significant cost and operational weight for the demo budget.
- **MassTransit:** the library went commercial in v9. The thin AWS SDK adapter keeps the mechanics visible.
