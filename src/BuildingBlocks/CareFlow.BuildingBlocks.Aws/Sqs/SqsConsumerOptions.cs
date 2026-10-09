using System.ComponentModel.DataAnnotations;

namespace CareFlow.BuildingBlocks.Aws.Sqs;

public sealed class SqsConsumerOptions
{
    [Required]
    public string QueueUrl { get; set; } = string.Empty;

    /// <summary>
    /// Where messages that can never succeed (unknown type, unreadable body) are sent immediately, instead of
    /// burning through every redelivery first. When empty, they are left to the queue's redrive policy.
    /// </summary>
    public string? DeadLetterQueueUrl { get; set; }

    [Range(1, 64)]
    public int MaxConcurrency { get; set; } = 4;

    [Range(0, 20)]
    public int WaitTimeSeconds { get; set; } = 20;

    /// <summary>Must match <c>maxReceiveCount</c> in the queue's redrive policy; used to report the final attempt.</summary>
    [Range(1, 1000)]
    public int MaxReceiveCount { get; set; } = 5;

    /// <summary>In-process retries of a failing handler before the message is handed back to SQS.</summary>
    [Range(0, 10)]
    public int HandlerRetryAttempts { get; set; } = 3;

    public TimeSpan HandlerRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Base of the exponential redelivery backoff: a failed message becomes visible again after
    /// <c>base * 2^(receiveCount - 1)</c>, capped at 15 minutes.
    /// </summary>
    public TimeSpan RedeliveryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Pause after the queue itself cannot be reached.</summary>
    public TimeSpan ReceiveErrorDelay { get; set; } = TimeSpan.FromSeconds(5);
}
