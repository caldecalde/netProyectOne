using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Application.Files;
using CareFlow.Ingestion.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CareFlow.Ingestion.UnitTests.Application;

public class ParseResultHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);

    private readonly Mock<IIngestionFileRepository> _repository = new(MockBehavior.Strict);
    private readonly IngestionFile _file = IngestionFile.Receive(Guid.NewGuid(), "claim.x12", FileFormat.X12, 100, new string('a', 64), "user-1", Now);

    [Fact]
    public async Task MessageParsed_ForReceivedFile_MarksItParsed()
    {
        _repository.Setup(r => r.GetAsync(_file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_file);
        _repository.Setup(r => r.SaveOutcomeAsync(_file, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateHandler().HandleAsync(Parsed(_file.Id), CancellationToken.None);

        Assert.Equal(IngestionStatus.Parsed, _file.Status);
        _repository.Verify(r => r.SaveOutcomeAsync(_file, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ParseFailed_ForReceivedFile_MarksItFailedWithTheReasonCode()
    {
        _repository.Setup(r => r.GetAsync(_file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_file);
        _repository.Setup(r => r.SaveOutcomeAsync(_file, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateHandler().HandleAsync(Failed(_file.Id, "x12.interchange_invalid"), CancellationToken.None);

        Assert.Equal(IngestionStatus.Failed, _file.Status);
        Assert.Equal("x12.interchange_invalid", _file.FailureReason);
    }

    [Fact]
    public async Task RedeliveredResult_ForFileAlreadyParsed_IsIgnored()
    {
        _file.MarkParsed();
        _repository.Setup(r => r.GetAsync(_file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_file);

        await CreateHandler().HandleAsync(Failed(_file.Id, "x12.interchange_invalid"), CancellationToken.None);

        Assert.Equal(IngestionStatus.Parsed, _file.Status);
        _repository.Verify(r => r.SaveOutcomeAsync(It.IsAny<IngestionFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Result_ForUnknownFile_IsAcknowledgedWithoutChanges()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((IngestionFile?)null);

        await CreateHandler().HandleAsync(Parsed(Guid.NewGuid()), CancellationToken.None);

        _repository.Verify(r => r.SaveOutcomeAsync(It.IsAny<IngestionFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ParseResultHandler CreateHandler() => new(_repository.Object, NullLogger<ParseResultHandler>.Instance);

    private static MessageParsed Parsed(Guid ingestionId) => new()
    {
        IngestionId = ingestionId,
        Format = "X12",
        MessageTypes = ["837P"],
        Bucket = "careflow-parsed",
        ResultKey = "parsed/x.json",
        AcceptedCount = 1,
        RejectedCount = 0,
    };

    private static ParseFailed Failed(Guid ingestionId, string reason) => new()
    {
        IngestionId = ingestionId,
        Format = "X12",
        ReasonCode = reason,
        IssueCount = 1,
    };
}
