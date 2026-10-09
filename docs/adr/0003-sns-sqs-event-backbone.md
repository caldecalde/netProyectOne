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

## Known gap (scheduled for Week 2)
Ingestion currently writes MongoDB and then publishes to SNS. If SNS is unavailable between those two steps, the file is stored but no event is sent. A **transactional outbox** will close this gap: the event is written in the same MongoDB transaction and relayed by a background publisher.

## Alternatives considered
- **EventBridge:** richer routing, but more than this system needs, and weaker LocalStack parity.
- **Kafka / MSK:** high throughput and replay, but it adds significant cost and operational weight for the demo budget.
- **MassTransit:** the library went commercial in v9. The thin AWS SDK adapter keeps the mechanics visible.
