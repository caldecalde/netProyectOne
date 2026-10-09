#!/usr/bin/env bash
# Runs inside LocalStack once it is ready (mounted into /etc/localstack/init/ready.d).
# Mirrors the resources Terraform creates on real AWS (deploy/terraform).
set -euo pipefail

REGION=us-east-1
RAW_BUCKET=careflow-raw
TOPIC=careflow-events

awslocal s3api create-bucket --bucket "$RAW_BUCKET" --region "$REGION"
awslocal s3api put-public-access-block --bucket "$RAW_BUCKET" \
  --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
awslocal s3api put-bucket-encryption --bucket "$RAW_BUCKET" \
  --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'

TOPIC_ARN=$(awslocal sns create-topic --name "$TOPIC" --query TopicArn --output text)

# One queue per consumer + event type, each with a dead-letter queue after 5 failed receives.
create_subscribed_queue() {
  local queue=$1 event_type=$2
  local dlq_url dlq_arn queue_url queue_arn
  dlq_url=$(awslocal sqs create-queue --queue-name "${queue}-dlq" --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  queue_url=$(awslocal sqs create-queue --queue-name "$queue" \
    --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\",\"VisibilityTimeout\":\"60\"}" \
    --query QueueUrl --output text)
  queue_arn=$(awslocal sqs get-queue-attributes --queue-url "$queue_url" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  awslocal sns subscribe --topic-arn "$TOPIC_ARN" --protocol sqs --notification-endpoint "$queue_arn" \
    --attributes "{\"RawMessageDelivery\":\"true\",\"FilterPolicy\":\"{\\\"event-type\\\":[\\\"${event_type}\\\"]}\"}" > /dev/null
  echo "queue ${queue} <- ${event_type}"
}

create_subscribed_queue parser-file-received FileReceived

echo "CareFlow LocalStack resources ready"
