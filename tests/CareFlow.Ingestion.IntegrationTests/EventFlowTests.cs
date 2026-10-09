using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.IntegrationTests.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CareFlow.Ingestion.IntegrationTests;

/// <summary>The transactional outbox, trace propagation, and status updates driven by parser results.</summary>
[Collection(IngestionApiTests.Name)]
public class EventFlowTests(IngestionApiFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Upload_EventCarriesTheRequestTraceAcrossTheOutbox()
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var client = fixture.CreateClientAs("clinic-user", "submitter");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/files") { Content = Upload(UniqueHl7()) };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        using var response = await client.SendAsync(request);
        var file = await response.Content.ReadFromJsonAsync<FileResponse>(Json);

        // Published later by the relay, outside the request, yet still in the caller's trace.
        var message = await fixture.WaitForMessageAsync(
            fixture.FileReceivedQueueUrl, m => m.Body.Contains(file!.Id.ToString(), StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        Assert.NotNull(message);
        var traceparent = message.MessageAttributes["traceparent"].StringValue;
        Assert.Equal(traceId.ToString(), traceparent.Split('-')[1]);

        var outbox = await fixture.Mongo.GetCollection<BsonDocument>("outbox")
            .Find(Builders<BsonDocument>.Filter.Eq("EventType", "FileReceived") & Builders<BsonDocument>.Filter.Regex("Payload", file!.Id.ToString()))
            .SingleAsync();
        Assert.Equal("Published", outbox["Status"].AsString);
    }

    [Fact]
    public async Task PendingOutboxMessage_LeftByACrashedInstance_IsPublishedByTheRelay()
    {
        // Simulates a process that committed the file and its event, then died before publishing.
        var integrationEvent = new FileReceived
        {
            OccurredAt = DateTimeOffset.UtcNow,
            IngestionId = Guid.NewGuid(),
            Format = "Hl7v2",
            Bucket = IngestionApiFixture.Bucket,
            StorageKey = "raw/2026/01/05/recovered.hl7",
            Sha256 = new string('d', 64),
            SizeBytes = 10,
        };
        var envelope = EventSerializer.Serialize(integrationEvent);
        var now = DateTime.UtcNow;
        await fixture.Mongo.GetCollection<BsonDocument>("outbox").InsertOneAsync(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(envelope.EventId, GuidRepresentation.Standard),
            ["EventType"] = envelope.EventType,
            ["Payload"] = envelope.Body,
            ["Status"] = "Pending",
            ["CreatedAt"] = now,
            ["AvailableAt"] = now,
            ["Attempts"] = 0,
        });

        // Make sure the API (and so its relay) is running.
        using var client = fixture.CreateClient();
        await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        var message = await fixture.WaitForMessageAsync(
            fixture.FileReceivedQueueUrl, m => m.Body.Contains(integrationEvent.IngestionId.ToString(), StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        Assert.NotNull(message);
        Assert.Equal(envelope.EventId.ToString(), message.MessageAttributes["event-id"].StringValue);
    }

    [Fact]
    public async Task ParserResults_UpdateTheFileStatus()
    {
        using var client = fixture.CreateClientAs("clinic-user", "submitter");
        var parsed = await SubmitAsync(client);
        var failed = await SubmitAsync(client);

        await PublishAsync(new MessageParsed
        {
            IngestionId = parsed.Id,
            Format = "Hl7v2",
            MessageTypes = ["ADT^A08"],
            Bucket = "careflow-parsed",
            ResultKey = "parsed/x.json",
            AcceptedCount = 1,
            RejectedCount = 0,
        });
        await PublishAsync(new ParseFailed { IngestionId = failed.Id, Format = "Hl7v2", ReasonCode = "hl7.no_accepted_messages", IssueCount = 1 });

        Assert.Equal(("Parsed", null), await WaitForStatusAsync(client, parsed.Id, "Parsed"));
        Assert.Equal(("Failed", "hl7.no_accepted_messages"), await WaitForStatusAsync(client, failed.Id, "Failed"));
    }

    private static async Task<FileResponse> SubmitAsync(HttpClient client)
    {
        using var response = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(UniqueHl7()));
        return (await response.Content.ReadFromJsonAsync<FileResponse>(Json))!;
    }

    private async Task PublishAsync(IntegrationEvent integrationEvent)
    {
        var envelope = EventSerializer.Serialize(integrationEvent);
        await fixture.Sns.PublishAsync(new PublishRequest
        {
            TopicArn = fixture.TopicArn,
            Message = envelope.Body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["event-type"] = new() { DataType = "String", StringValue = envelope.EventType },
            },
        });
    }

    private static async Task<(string Status, string? FailureReason)> WaitForStatusAsync(HttpClient client, Guid id, string expected)
    {
        FileResponse? file = null;
        for (var attempt = 0; attempt < 30 && file?.Status != expected; attempt++)
        {
            await Task.Delay(500);
            file = await client.GetFromJsonAsync<FileResponse>($"/api/v1/files/{id}", Json);
        }

        return (file!.Status, file.FailureReason);
    }

    private static MultipartFormDataContent Upload(byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", "upload.hl7" } };
    }

    private static byte[] UniqueHl7() => Encoding.ASCII.GetBytes(
        $"MSH|^~\\&|TEST|CLINIC|CAREFLOW|CAREFLOW|20260105083000||ADT^A08|{Guid.NewGuid():N}|P|2.5.1\rPID|1||PAT-1\r");

    private sealed record FileResponse(Guid Id, string Status, string? FailureReason);
}
