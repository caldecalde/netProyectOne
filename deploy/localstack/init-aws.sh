#!/usr/bin/env bash
# Runs inside LocalStack once it is ready (mounted into /etc/localstack/init/ready.d).
# Mirrors the resources Terraform creates on real AWS (deploy/terraform).
set -euo pipefail

REGION=us-east-1
RAW_BUCKET=careflow-raw
PARSED_BUCKET=careflow-parsed
TOPIC=careflow-events

# Both buckets hold PHI: no public access, encrypted by default.
create_private_bucket() {
  local bucket=$1
  awslocal s3api create-bucket --bucket "$bucket" --region "$REGION"
  awslocal s3api put-public-access-block --bucket "$bucket" \
    --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
  awslocal s3api put-bucket-encryption --bucket "$bucket" \
    --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
}

create_private_bucket "$RAW_BUCKET"
create_private_bucket "$PARSED_BUCKET"

TOPIC_ARN=$(awslocal sns create-topic --name "$TOPIC" --query TopicArn --output text)

# One queue per consumer, subscribed to the event types it handles, with a dead-letter queue after 5 failed receives.
# Usage: create_subscribed_queue <queue> <event-type> [<event-type> ...]
create_subscribed_queue() {
  local queue=$1
  shift
  local event_types dlq_url dlq_arn queue_url queue_arn
  event_types=$(printf '\\"%s\\",' "$@")
  event_types=${event_types%,}

  dlq_url=$(awslocal sqs create-queue --queue-name "${queue}-dlq" \
    --attributes '{"MessageRetentionPeriod":"1209600"}' --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  queue_url=$(awslocal sqs create-queue --queue-name "$queue" \
    --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\",\"VisibilityTimeout\":\"60\"}" \
    --query QueueUrl --output text)
  queue_arn=$(awslocal sqs get-queue-attributes --queue-url "$queue_url" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  awslocal sns subscribe --topic-arn "$TOPIC_ARN" --protocol sqs --notification-endpoint "$queue_arn" \
    --attributes "{\"RawMessageDelivery\":\"true\",\"FilterPolicy\":\"{\\\"event-type\\\":[${event_types}]}\"}" > /dev/null
  echo "queue ${queue} <- $*"
}

create_subscribed_queue parser-file-received FileReceived
create_subscribed_queue ingestion-parse-results MessageParsed ParseFailed

echo "CareFlow LocalStack resources ready"
