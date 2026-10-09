using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Parser.Application;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Application.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CareFlow.Parser.UnitTests.Application;

public class ParseFileHandlerTests
{
    private const string Bucket = "careflow-raw";
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Claim = File.ReadAllBytes("samples/x12/837p-single-claim.x12");

    private readonly Mock<IRawFileReader> _rawFiles = new(MockBehavior.Strict);
    private readonly Mock<IParseResultStore> _results = new();
    private readonly Mock<IProcessedEventStore> _processed = new();
    private readonly Mock<IEventPublisher> _publisher = new();
    private readonly Dictionary<string, byte[]> _stored = [];
    private IntegrationEvent? _published;

    public ParseFileHandlerTests()
    {
        _results.SetupGet(r => r.Location).Returns("careflow-parsed");
        _results
            .Setup(r => r.PutAsync(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, ReadOnlyMemory<byte>, string, IReadOnlyDictionary<string, string>, CancellationToken>((key, content, _, _, _) => _stored[key] = content.ToArray())
            .Returns(Task.CompletedTask);
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<IntegrationEvent>(), It.IsAny<CancellationToken>()))
            .Callback<IntegrationEvent, CancellationToken>((e, _) => _published = e)
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ValidClaim_StoresDocumentAnd999_PublishesMessageParsed_AndRecordsTheEvent()
    {
        var received = FileReceivedFor(Claim);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync(Claim);

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        var parsed = Assert.IsType<MessageParsed>(_published);
        var keySuffix = $"2026/01/05/{received.IngestionId:N}";
        Assert.Equal($"parsed/{keySuffix}.json", parsed.ResultKey);
        Assert.Equal($"acks/{keySuffix}.999", parsed.AcknowledgmentKey);
        Assert.Equal("careflow-parsed", parsed.Bucket);
        Assert.Equal(["837P"], parsed.MessageTypes);
        Assert.Equal((1, 0), (parsed.AcceptedCount, parsed.RejectedCount));
        Assert.Equal(received.EventId, parsed.CausationId);

        using var document = JsonDocument.Parse(_stored[parsed.ResultKey]);
        Assert.Equal("Parsed", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("CLM-000123", document.RootElement.GetProperty("messages")[0].GetProperty("claims")[0].GetProperty("claimId").GetString());
        Assert.StartsWith("ISA*", Encoding.UTF8.GetString(_stored[parsed.AcknowledgmentKey!]), StringComparison.Ordinal);

        _processed.Verify(p => p.MarkProcessedAsync(received.EventId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Redelivery_PublishesTheSameEventId()
    {
        var received = FileReceivedFor(Claim);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync(Claim);

        await CreateHandler().HandleAsync(received, CancellationToken.None);
        var first = _published!.EventId;
        await CreateHandler().HandleAsync(received, CancellationToken.None);

        Assert.Equal(first, _published!.EventId);
    }

    [Fact]
    public async Task AlreadyProcessedEvent_IsSkipped()
    {
        var received = FileReceivedFor(Claim);
        _processed.Setup(p => p.IsProcessedAsync(received.EventId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        Assert.Null(_published);
        _rawFiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ContentNotMatchingItsHash_FailsWithoutParsing()
    {
        var received = FileReceivedFor(Claim);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync([.. Claim, (byte)'X']);

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        var failed = Assert.IsType<ParseFailed>(_published);
        Assert.Equal("parser.integrity_mismatch", failed.ReasonCode);
        Assert.Null(failed.ResultKey);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task EventPointingOutsideTheSourceBucket_IsRejectedWithoutReading()
    {
        var received = FileReceivedFor(Claim) with { Bucket = "someone-elses-bucket" };

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        Assert.Equal("parser.untrusted_location", Assert.IsType<ParseFailed>(_published).ReasonCode);
        _rawFiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingRawFile_Fails()
    {
        var received = FileReceivedFor(Claim);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        Assert.Equal("parser.raw_file_missing", Assert.IsType<ParseFailed>(_published).ReasonCode);
    }

    [Fact]
    public async Task UnparseableContent_PublishesParseFailedThatPointsAtTheIssues()
    {
        var content = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Claim).Replace("IEA*1*000000001", "IEA*1*000000777", StringComparison.Ordinal));
        var received = FileReceivedFor(content);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync(content);

        await CreateHandler().HandleAsync(received, CancellationToken.None);

        var failed = Assert.IsType<ParseFailed>(_published);
        Assert.Equal("x12.interchange_invalid", failed.ReasonCode);
        Assert.Equal(1, failed.IssueCount);
        Assert.NotNull(failed.ResultKey);
        Assert.Null(failed.AcknowledgmentKey);
        _processed.Verify(p => p.MarkProcessedAsync(received.EventId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishFailure_PropagatesAndDoesNotRecordTheEvent()
    {
        // The consumer then retries the message; marking it processed first would lose the result.
        var received = FileReceivedFor(Claim);
        _rawFiles.Setup(r => r.ReadAsync(Bucket, received.StorageKey, It.IsAny<CancellationToken>())).ReturnsAsync(Claim);
        _publisher.Setup(p => p.PublishAsync(It.IsAny<IntegrationEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException());

        await Assert.ThrowsAsync<TimeoutException>(() => CreateHandler().HandleAsync(received, CancellationToken.None));

        _processed.Verify(p => p.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ParseFileHandler CreateHandler() => new(
        [new X12FileParser()],
        _rawFiles.Object,
        _results.Object,
        _processed.Object,
        _publisher.Object,
        new FixedTimeProvider(Now),
        Options.Create(new ParserOptions { SourceBucket = Bucket }),
        NullLogger<ParseFileHandler>.Instance);

    private static FileReceived FileReceivedFor(byte[] content)
    {
        var id = Guid.NewGuid();
        return new FileReceived
        {
            OccurredAt = Now,
            IngestionId = id,
            Format = "X12",
            Bucket = Bucket,
            StorageKey = $"raw/2026/01/05/{id:N}.x12",
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            SizeBytes = content.Length,
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
