<#
.SYNOPSIS
  Inspect or redrive a CareFlow dead-letter queue (PowerShell twin of dlq.sh).
.DESCRIPTION
  inspect: shows the DLQ depth and, for up to 10 messages, the event type, event ID and why it was dead-lettered.
           Message bodies are never printed.
  redrive: moves every message back to the source queue, typically after deploying a fix. Handlers are idempotent,
           so events that were already processed are skipped.
  Targets the local LocalStack by default; pass -Aws to use the AWS CLI with your current profile.
.EXAMPLE
  ./scripts/dlq.ps1 inspect parser-file-received
  ./scripts/dlq.ps1 redrive ingestion-parse-results -Aws
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('inspect', 'redrive')][string]$Action,
    [Parameter(Mandatory)][string]$Queue,
    [switch]$Aws
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Invoke-AwsCli {
    if ($Aws) { $output = & aws @args }
    else { $output = & docker compose -f (Join-Path $root 'deploy/docker-compose.yml') exec -T localstack awslocal @args }
    if ($LASTEXITCODE -ne 0) { throw "aws $($args -join ' ') failed" }
    $output
}

function Get-QueueArn([string]$Url) {
    Invoke-AwsCli sqs get-queue-attributes --queue-url $Url --attribute-names QueueArn --query Attributes.QueueArn --output text
}

$dlqUrl = Invoke-AwsCli sqs get-queue-url --queue-name "$Queue-dlq" --query QueueUrl --output text
$depth = Invoke-AwsCli sqs get-queue-attributes --queue-url $dlqUrl --attribute-names ApproximateNumberOfMessages `
    --query Attributes.ApproximateNumberOfMessages --output text
Write-Host "$Queue-dlq: $depth message(s)"

if ($Action -eq 'inspect') {
    # Peek without consuming: a zero visibility timeout leaves the messages available.
    $json = Invoke-AwsCli sqs receive-message --queue-url $dlqUrl --max-number-of-messages 10 --visibility-timeout 0 `
        --message-attribute-names All --attribute-names ApproximateReceiveCount --output json
    $messages = if ($json) { ($json | Out-String | ConvertFrom-Json).Messages } else { @() }
    $messages | ForEach-Object {
        [pscustomobject]@{
            Event    = $_.MessageAttributes.'event-type'.StringValue
            EventId  = $_.MessageAttributes.'event-id'.StringValue
            Reason   = $_.MessageAttributes.'careflow-dead-letter-reason'.StringValue
            Receives = $_.Attributes.ApproximateReceiveCount
        }
    } | Format-Table -AutoSize
    Write-Host 'No reason = the handler kept failing until the redrive policy moved it (see the service logs for the event ID).'
    return
}

if ($depth -eq '0') { Write-Host 'Nothing to redrive.'; return }

# The destination is explicit: messages the consumer dead-lettered itself (poison) have no SQS-recorded source queue.
$sourceUrl = Invoke-AwsCli sqs get-queue-url --queue-name $Queue --query QueueUrl --output text
$task = Invoke-AwsCli sqs start-message-move-task --source-arn (Get-QueueArn $dlqUrl) --destination-arn (Get-QueueArn $sourceUrl) `
    --query TaskHandle --output text
Write-Host "Redrive started (task $task). Handlers are idempotent, so already-processed events are skipped."
