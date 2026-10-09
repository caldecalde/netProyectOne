# 0005 – Transactional outbox, idempotent consumers, and dead-letter handling

**Status:** Accepted

## Context
SNS → SQS ([ADR 0003](0003-sns-sqs-event-backbone.md)) delivers at least once, but only for messages that reach SNS. A service that updates its database and then publishes can crash, or lose the broker, between the two steps. On the consuming side, the same message can arrive twice, a handler can fail for reasons that go away on their own (S3 throttling, MongoDB failover), and some messages can never succeed (wrong type, unreadable body). Retrying those forever wastes capacity and hides the problem.

## Decision

**Producer: transactional outbox (Ingestion).**
- `SubmitFileHandler` hands the `FileReceived` event to the repository, which inserts the file record and an `outbox` document in **one MongoDB transaction**. Locally MongoDB runs as a single-node replica set, because standalone `mongod` has no transactions.
- `OutboxRelay`, a background service, publishes pending messages to SNS. A signal wakes it as soon as a commit happens, and polling (every 5 s) picks up anything left behind by a crashed instance.
- Messages are claimed with a short **lease** (`AvailableAt`), so several replicas can relay side by side without publishing the same message concurrently. Failed publishes back off exponentially (capped at 5 minutes) and are retried indefinitely: no event is ever dropped. Published messages expire after 7 days (TTL index).
- The outbox row stores the request's W3C `traceparent`. The relay publishes inside that context, so the trace continues from the HTTP upload to every consumer even though publishing happens later, in another thread.

**Consumers: one shared SQS pump (`BuildingBlocks.Aws`).**
- The pump dispatches on the `event-type` attribute and continues the producer's trace from `traceparent`.
- **Success** deletes the message.
- **Failure** is retried in process with Polly (3 attempts, exponential backoff with jitter). If it still fails, the message goes back to SQS with an exponential visibility timeout (10 s, 20 s, 40 s, ...). After `maxReceiveCount = 5` receives, the queue's redrive policy moves it to the DLQ. On the final attempt the message is released immediately, so it reaches the DLQ without waiting.
- **Poison messages** (unknown event type, unreadable body, a required field missing) go straight to the DLQ with a `careflow-dead-letter-reason` attribute, instead of burning five deliveries. Bodies are never logged.
- **Content problems are outcomes, not failures.** An HL7 or X12 file that cannot be parsed produces `ParseFailed` and the message is deleted. The DLQ is reserved for things an operator has to look at: infrastructure that stayed down, or a parser bug.

**Idempotency.**
- The parser records processed `EventId`s (an inbox with a 14-day TTL) and skips repeats. Its output keys depend only on the ingestion ID, and the ID of its result event is derived from the incoming event ID (UUID v5). A redelivery that slips past the inbox, for example two replicas processing the same message at once, rewrites the same objects and republishes the same event ID.
- Ingestion applies a parse result only while the file is still `Received`, with a conditional update, so duplicates and races are no-ops.
- The parser publishes results directly, without an outbox. SQS deletes the input message only after the publish succeeds, so a failed publish is retried by redelivery.

**Operations.** `scripts/dlq.{sh,ps1} inspect <queue>` shows DLQ depth plus each message's type, event ID and dead-letter reason. `redrive <queue>` moves the messages back after a fix (SQS `StartMessageMoveTask`). On AWS, a CloudWatch alarm on each DLQ's `ApproximateNumberOfMessagesVisible > 0` is added with Terraform (Week 4).

## Consequences
- Publishing is **eventually** consistent with the database: a few milliseconds normally, longer during a broker outage. Each event is delivered at least once and never lost. Consumers must de-duplicate, and they do.
- MongoDB must run as a replica set in every environment (Atlas always does).
- Event order is not guaranteed. Nothing in the current flow depends on it. A future flow that does would need per-key ordering (SQS FIFO with a message group per ingestion).

## Alternatives considered
- **Publish first, then write:** an orphan event can point at a file that was never recorded.
- **Embedding pending events in the file document** (single-document atomicity, no transaction): this works without a replica set, but it couples the relay to every aggregate's collection.
- **Change streams as the relay:** these need a replica set too, and add resume-token management for little gain at this scale.
