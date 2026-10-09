using System.Text.Json;
using CareFlow.BuildingBlocks.Messaging;

namespace CareFlow.BuildingBlocks.UnitTests;

public class MessagingTests
{
    [Fact]
    public void DeterministicGuid_IsStableVersion5AndNameSensitive()
    {
        var ns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // RFC 9562 DNS namespace

        var guid = DeterministicGuid.Create(ns, "www.example.com");

        // Reference value from RFC 9562 appendix / Python uuid.uuid5(NAMESPACE_DNS, "www.example.com").
        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), guid);
        Assert.Equal(guid, DeterministicGuid.Create(ns, "www.example.com"));
        Assert.NotEqual(guid, DeterministicGuid.Create(ns, "www.example.org"));
    }

    [Fact]
    public void Serialize_UsesRuntimeType_SoBaseTypedEventsKeepTheirFields()
    {
        IntegrationEvent integrationEvent = new FileReceived
        {
            IngestionId = Guid.NewGuid(),
            Format = "X12",
            Bucket = "b",
            StorageKey = "k",
            Sha256 = "h",
            SizeBytes = 42,
        };

        var envelope = EventSerializer.Serialize(integrationEvent);

        Assert.Equal("FileReceived", envelope.EventType);
        Assert.Equal(integrationEvent.EventId, envelope.EventId);
        var roundTrip = Assert.IsType<FileReceived>(EventSerializer.Deserialize(envelope.Body, typeof(FileReceived)));
        Assert.Equal(integrationEvent, roundTrip);
    }

    [Fact]
    public void Deserialize_MissingRequiredField_Throws()
    {
        Assert.Throws<JsonException>(() => EventSerializer.Deserialize("""{"format":"X12"}""", typeof(FileReceived)));
    }
}
