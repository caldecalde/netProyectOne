using Amazon.SQS;
using Amazon.SQS.Model;
using CareFlow.BuildingBlocks.Aws;
using CareFlow.BuildingBlocks.Aws.Sqs;
using CareFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CareFlow.BuildingBlocks.UnitTests;

public class SqsMessagePumpTests
{
    private const string QueueUrl = "http://sqs.local/000000000000/test-queue";
    private const string DlqUrl = "http://sqs.local/000000000000/test-queue-dlq";

    private readonly Mock<IAmazonSQS> _sqs = new();
    private readonly RecordingHandler _handler = new();

    [Fact]
    public async Task HandledMessage_IsDeleted()
    {
        var integrationEvent = SampleEvent();

        await CreatePump().ProcessAsync(Message(EventSerializer.Serialize(integrationEvent).Body), CancellationToken.None);

        Assert.Equal(integrationEvent.EventId, Assert.Single(_handler.Received).EventId);
        _sqs.Verify(s => s.DeleteMessageAsync(QueueUrl, "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailingHandler_IsRetriedInProcessThenReturnedWithBackoff()
    {
        _handler.FailuresBeforeSuccess = int.MaxValue;

        await CreatePump().ProcessAsync(Message(EventSerializer.Serialize(SampleEvent()).Body, receiveCount: 2), CancellationToken.None);

        Assert.Equal(3, _handler.Attempts); // first try + 2 in-process retries
        _sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync(QueueUrl, "receipt-1", 20, It.IsAny<CancellationToken>()), Times.Once); // 10s * 2^(2-1)
    }

    [Fact]
    public async Task TransientFailure_RecoveredByInProcessRetry_IsDeleted()
    {
        _handler.FailuresBeforeSuccess = 1;

        await CreatePump().ProcessAsync(Message(EventSerializer.Serialize(SampleEvent()).Body), CancellationToken.None);

        Assert.Equal(2, _handler.Attempts);
        _sqs.Verify(s => s.DeleteMessageAsync(QueueUrl, "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalAttemptFailure_ReleasesMessageImmediatelyForTheRedrivePolicy()
    {
        _handler.FailuresBeforeSuccess = int.MaxValue;

        await CreatePump().ProcessAsync(Message(EventSerializer.Serialize(SampleEvent()).Body, receiveCount: 5), CancellationToken.None);

        _sqs.Verify(s => s.ChangeMessageVisibilityAsync(QueueUrl, "receipt-1", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("SomethingElse", """{"eventId":"0b1c7a4e-2a43-4a39-9d0f-3b1f1f5f2c11"}""", "unknown_event_type")]
    [InlineData("TestEvent", "not json", "invalid_body")]
    [InlineData("TestEvent", """{"eventId":"0b1c7a4e-2a43-4a39-9d0f-3b1f1f5f2c11"}""", "invalid_body")] // required field missing
    public async Task PoisonMessage_GoesStraightToTheDeadLetterQueue(string eventType, string body, string expectedReason)
    {
        SendMessageRequest? sent = null;
        _sqs.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new SendMessageResponse());

        await CreatePump().ProcessAsync(Message(body, eventType: eventType), CancellationToken.None);

        Assert.Empty(_handler.Received);
        Assert.NotNull(sent);
        Assert.Equal(DlqUrl, sent.QueueUrl);
        Assert.Equal(body, sent.MessageBody);
        Assert.Equal(expectedReason, sent.MessageAttributes[MessageAttributes.DeadLetterReason].StringValue);
        Assert.Equal("test-queue", sent.MessageAttributes[MessageAttributes.SourceQueue].StringValue);
        Assert.Equal(eventType, sent.MessageAttributes[MessageAttributes.EventType].StringValue); // original attributes kept
        _sqs.Verify(s => s.DeleteMessageAsync(QueueUrl, "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueFailure_DoesNotEscapeThePump()
    {
        _sqs.Setup(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("receipt handle expired"));

        var exception = await Record.ExceptionAsync(() => CreatePump().ProcessAsync(Message(EventSerializer.Serialize(SampleEvent()).Body), CancellationToken.None));

        Assert.Null(exception);
    }

    private SqsMessagePump CreatePump()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_sqs.Object);
        services.AddSingleton(_handler);
        services.AddSqsConsumer("test-queue")
            .Configure(o =>
            {
                o.QueueUrl = QueueUrl;
                o.DeadLetterQueueUrl = DlqUrl;
                o.HandlerRetryAttempts = 2;
                o.HandlerRetryDelay = TimeSpan.Zero;
            })
            .Handle<TestEvent, RecordingHandlerAdapter>();

        return (SqsMessagePump)services.BuildServiceProvider().GetServices<IHostedService>().Single();
    }

    private static TestEvent SampleEvent() => new() { OccurredAt = DateTimeOffset.UnixEpoch, Value = "x" };

    private static Message Message(string body, string eventType = "TestEvent", int receiveCount = 1) => new()
    {
        MessageId = "message-1",
        ReceiptHandle = "receipt-1",
        Body = body,
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            [MessageAttributes.EventType] = new() { DataType = "String", StringValue = eventType },
            [MessageAttributes.TraceParent] = new() { DataType = "String", StringValue = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" },
        },
        Attributes = new Dictionary<string, string> { ["ApproximateReceiveCount"] = receiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
    };

    public sealed record TestEvent : IntegrationEvent
    {
        public required string Value { get; init; }
    }

    public sealed class RecordingHandler
    {
        public List<TestEvent> Received { get; } = [];

        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; set; }

        public Task HandleAsync(TestEvent integrationEvent)
        {
            Attempts++;
            if (Attempts <= FailuresBeforeSuccess)
            {
                throw new TimeoutException("downstream unavailable");
            }

            Received.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingHandlerAdapter(RecordingHandler handler) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent integrationEvent, CancellationToken cancellationToken) => handler.HandleAsync(integrationEvent);
    }
}
