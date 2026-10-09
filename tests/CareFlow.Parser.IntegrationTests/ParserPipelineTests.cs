using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SQS.Model;
using CareFlow.BuildingBlocks.Messaging;

namespace CareFlow.Parser.IntegrationTests;

[Collection(ParserWorkerTests.Name)]
public class ParserPipelineTests(ParserWorkerFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task X12Claim_IsParsed_ResultAnd999Stored_AndTheTraceContinues()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var received = await fixture.SubmitAsync(
            await File.ReadAllBytesAsync("samples/x12/837p-batch-partial.x12"), "X12", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var message = Assert.Single(await ResultsFor(received));
        var parsed = Deserialize<MessageParsed>(message);
        Assert.Equal((1, 1), (parsed.AcceptedCount, parsed.RejectedCount));
        Assert.Equal(received.EventId, parsed.CausationId);

        // The parser's result is published in the trace started by the producer of FileReceived.
        Assert.Equal(traceId.ToString(), message.MessageAttributes["traceparent"].StringValue.Split('-')[1]);

        using var document = JsonDocument.Parse(await fixture.ReadResultAsync(parsed.ResultKey));
        Assert.Equal(2, document.RootElement.GetProperty("messages")[0].GetProperty("claims").GetArrayLength());
        Assert.Contains("AK9*P*2*2*1~", await fixture.ReadResultAsync(parsed.AcknowledgmentKey!), StringComparison.Ordinal);

        var encryption = await fixture.S3.GetObjectMetadataAsync(ParserWorkerFixture.ResultsBucket, parsed.ResultKey);
        Assert.Equal(Amazon.S3.ServerSideEncryptionMethod.AES256, encryption.ServerSideEncryptionMethod);
    }

    [Fact]
    public async Task Hl7Batch_IsParsedIntoEachMessage()
    {
        var received = await fixture.SubmitAsync(await File.ReadAllBytesAsync("samples/hl7/adt-batch.hl7"), "Hl7v2");

        var parsed = Deserialize<MessageParsed>(Assert.Single(await ResultsFor(received)));

        Assert.Equal(["ADT^A08", "ADT^A04"], parsed.MessageTypes);
        Assert.Equal(2, parsed.AcceptedCount);
        Assert.Null(parsed.AcknowledgmentKey);
    }

    [Fact]
    public async Task UnusableContent_PublishesParseFailed()
    {
        var received = await fixture.SubmitAsync("MSH|^~\\&|A|B|C|D|20260105||SIU^S12|X1|P|2.5.1\r"u8.ToArray(), "Hl7v2");

        var failed = Deserialize<ParseFailed>(Assert.Single(await ResultsFor(received)));

        Assert.Equal("hl7.no_accepted_messages", failed.ReasonCode);
        Assert.Contains("hl7.unsupported_message_type", await fixture.ReadResultAsync(failed.ResultKey!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeliveredEvent_IsProcessedOnce()
    {
        var received = await fixture.SubmitAsync(await File.ReadAllBytesAsync("samples/hl7/oru-r01.hl7"), "Hl7v2");
        Assert.Single(await ResultsFor(received));

        // At-least-once delivery: the same event arrives again.
        await fixture.PublishAsync(received);

        Assert.Empty(await ResultsFor(received, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PoisonMessage_IsMovedToTheDeadLetterQueueWithItsReason()
    {
        await fixture.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = fixture.InputQueueUrl,
            MessageBody = "{\"not\":\"a FileReceived\"}",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["event-type"] = new() { DataType = "String", StringValue = "FileReceived" },
            },
        });

        var deadLettered = Assert.Single(await fixture.ReceiveAsync(
            fixture.DeadLetterQueueUrl, m => m.Body.Contains("not", StringComparison.Ordinal), Timeout));

        Assert.Equal("invalid_body", deadLettered.MessageAttributes["careflow-dead-letter-reason"].StringValue);
        Assert.Equal("parser-file-received", deadLettered.MessageAttributes["careflow-source-queue"].StringValue);
    }

    [Fact]
    public async Task ReadinessProbe_ChecksMongoS3AndTheQueue()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<List<Message>> ResultsFor(FileReceived received, TimeSpan? timeout = null) =>
        fixture.ReceiveAsync(
            fixture.ResultsQueueUrl,
            m => m.Body.Contains(received.IngestionId.ToString(), StringComparison.Ordinal),
            timeout ?? Timeout,
            expected: timeout is null ? 1 : int.MaxValue);

    private static T Deserialize<T>(Message message)
        where T : IntegrationEvent =>
        (T)EventSerializer.Deserialize(message.Body, typeof(T));
}
