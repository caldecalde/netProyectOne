#!/usr/bin/env bash
# Inspect or redrive a CareFlow dead-letter queue (Bash twin of dlq.ps1).
#
#   ./scripts/dlq.sh inspect parser-file-received   # depth + why messages were dead-lettered (bodies are never printed)
#   ./scripts/dlq.sh redrive parser-file-received   # move every message back to the source queue, after a fix
#
# Targets the local LocalStack by default. Set CAREFLOW_AWS=aws to use the AWS CLI with your current profile.
set -euo pipefail

ACTION=${1:-}
QUEUE=${2:-}
if [[ ! "$ACTION" =~ ^(inspect|redrive)$ || -z "$QUEUE" ]]; then
  echo "usage: $0 inspect|redrive <queue-name>   e.g. $0 inspect parser-file-received" >&2
  exit 2
fi

ROOT=$(cd "$(dirname "$0")/.." && pwd)
aws_cli() {
  if [ "${CAREFLOW_AWS:-local}" = "aws" ]; then
    aws "$@"
  else
    docker compose -f "$ROOT/deploy/docker-compose.yml" exec -T localstack awslocal "$@"
  fi
}

DLQ_URL=$(aws_cli sqs get-queue-url --queue-name "${QUEUE}-dlq" --query QueueUrl --output text)
DLQ_ARN=$(aws_cli sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names QueueArn --query Attributes.QueueArn --output text)
DEPTH=$(aws_cli sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names ApproximateNumberOfMessages \
  --query Attributes.ApproximateNumberOfMessages --output text)
echo "${QUEUE}-dlq: ${DEPTH} message(s)"

if [ "$ACTION" = "inspect" ]; then
  # Peek without consuming: a zero visibility timeout leaves the messages available.
  aws_cli sqs receive-message --queue-url "$DLQ_URL" --max-number-of-messages 10 --visibility-timeout 0 \
    --message-attribute-names All --attribute-names ApproximateReceiveCount \
    --query 'Messages[].{event: MessageAttributes."event-type".StringValue, eventId: MessageAttributes."event-id".StringValue, reason: MessageAttributes."careflow-dead-letter-reason".StringValue, receives: Attributes.ApproximateReceiveCount}' \
    --output table
  echo "No reason = the handler kept failing until the redrive policy moved it (see the service logs for the event ID)."
  exit 0
fi

if [ "$DEPTH" = "0" ]; then
  echo "Nothing to redrive."
  exit 0
fi

# The destination is explicit: messages the consumer dead-lettered itself (poison) have no SQS-recorded source queue.
SOURCE_URL=$(aws_cli sqs get-queue-url --queue-name "$QUEUE" --query QueueUrl --output text)
SOURCE_ARN=$(aws_cli sqs get-queue-attributes --queue-url "$SOURCE_URL" --attribute-names QueueArn --query Attributes.QueueArn --output text)
TASK=$(aws_cli sqs start-message-move-task --source-arn "$DLQ_ARN" --destination-arn "$SOURCE_ARN" --query TaskHandle --output text)
echo "Redrive started (task ${TASK}). Handlers are idempotent, so already-processed events are skipped."
