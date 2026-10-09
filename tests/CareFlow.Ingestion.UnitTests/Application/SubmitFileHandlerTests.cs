using System.Text;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Application.Files;
using CareFlow.Ingestion.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CareFlow.Ingestion.UnitTests.Application;

public class SubmitFileHandlerTests
{
    private const string Hl7 = "MSH|^~\\&|SYNTHEA|CLINIC|CAREFLOW|CAREFLOW|20260105083000||ADT^A01|MSG1|P|2.5.1\rPID|1||PAT-1\r";
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);

    private readonly Mock<IRawFileStore> _store = new(MockBehavior.Strict);
    private readonly Mock<IIngestionFileRepository> _repository = new(MockBehavior.Strict);

    public SubmitFileHandlerTests()
    {
        _store.SetupGet(s => s.Location).Returns("careflow-raw");
    }

    [Fact]
    public async Task HandleAsync_NewHl7File_StoresContentAndCommitsRecordWithItsEventInTheOutbox()
    {
        byte[]? stored = null;
        _repository.Setup(r => r.FindBySha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((IngestionFile?)null);
        _store
            .Setup(s => s.PutAsync(It.IsAny<string>(), It.IsAny<Stream>(), "x-application/hl7-v2+er7", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, s, _, _, _) =>
            {
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                stored = copy.ToArray();
            })
            .Returns(Task.CompletedTask);
        IngestionFile? added = null;
        IReadOnlyCollection<IntegrationEvent>? outbox = null;
        _repository
            .Setup(r => r.TryAddAsync(It.IsAny<IngestionFile>(), It.IsAny<IReadOnlyCollection<IntegrationEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IngestionFile, IReadOnlyCollection<IntegrationEvent>, CancellationToken>((f, e, _) => (added, outbox) = (f, e))
            .ReturnsAsync(true);

        var result = await CreateHandler().HandleAsync(Command(Hl7, "adt.hl7"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsDuplicate);
        Assert.Equal("Hl7v2", result.Value.File.Format);
        Assert.Equal(Encoding.UTF8.GetBytes(Hl7), stored);
        Assert.Equal(result.Value.File.Id, added?.Id);

        // The event is handed to the repository, to be committed in the same transaction as the record.
        var published = Assert.IsType<FileReceived>(Assert.Single(outbox!));
        Assert.Equal(result.Value.File.Id, published.IngestionId);
        Assert.Equal("careflow-raw", published.Bucket);
        Assert.Equal(result.Value.File.Sha256, published.Sha256);
        Assert.StartsWith("raw/2026/01/05/", published.StorageKey, StringComparison.Ordinal);
        Assert.Equal(Now, published.OccurredAt);
    }

    [Fact]
    public async Task HandleAsync_SameContentAlreadySubmitted_ReturnsExistingWithoutSideEffects()
    {
        var existing = IngestionFile.Receive(Guid.NewGuid(), "adt.hl7", FileFormat.Hl7v2, 10, new string('b', 64), "user-1", Now);
        _repository.Setup(r => r.FindBySha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().HandleAsync(Command(Hl7), CancellationToken.None);

        Assert.True(result.Value.IsDuplicate);
        Assert.Equal(existing.Id, result.Value.File.Id);
        _store.Verify(s => s.PutAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.TryAddAsync(It.IsAny<IngestionFile>(), It.IsAny<IReadOnlyCollection<IntegrationEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_LosesInsertRace_ReturnsWinner()
    {
        var winner = IngestionFile.Receive(Guid.NewGuid(), "adt.hl7", FileFormat.Hl7v2, 10, new string('c', 64), "user-2", Now);
        _repository.SetupSequence(r => r.FindBySha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestionFile?)null)
            .ReturnsAsync(winner);
        _store.Setup(s => s.PutAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // The repository rejected the insert, so nothing (neither record nor event) was committed for this upload.
        _repository.Setup(r => r.TryAddAsync(It.IsAny<IngestionFile>(), It.IsAny<IReadOnlyCollection<IntegrationEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().HandleAsync(Command(Hl7), CancellationToken.None);

        Assert.True(result.Value.IsDuplicate);
        Assert.Equal(winner.Id, result.Value.File.Id);
    }

    [Theory]
    [InlineData("", "ingestion.empty_file")]
    [InlineData("{\"resourceType\":\"Patient\"}", "ingestion.unsupported_format")]
    public async Task HandleAsync_InvalidContent_ReturnsValidationErrorWithoutSideEffects(string content, string expectedCode)
    {
        var result = await CreateHandler().HandleAsync(Command(content), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error!.Code);
        _store.VerifyNoOtherCalls();
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_FileOverLimit_IsRejectedBeforeAnyIo()
    {
        var result = await CreateHandler(maxFileBytes: 16).HandleAsync(Command(Hl7), CancellationToken.None);

        Assert.Equal("ingestion.too_large", result.Error!.Code);
        _repository.VerifyNoOtherCalls();
    }

    private SubmitFileHandler CreateHandler(long maxFileBytes = 1024 * 1024) => new(
        _store.Object,
        _repository.Object,
        new FixedTimeProvider(Now),
        Options.Create(new IngestionOptions { MaxFileBytes = maxFileBytes }),
        NullLogger<SubmitFileHandler>.Instance);

    private static SubmitFileCommand Command(string content, string fileName = "upload") =>
        new(fileName, new MemoryStream(Encoding.UTF8.GetBytes(content)), "user-1");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
