using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CareFlow.BuildingBlocks.Observability;

/// <summary>Custom spans and business metrics shared by all CareFlow services.</summary>
public static class CareFlowTelemetry
{
    public const string ActivitySourceName = "CareFlow";
    public const string MeterName = "CareFlow";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> FilesReceived =
        Meter.CreateCounter<long>("careflow.files.received", unit: "{file}", description: "Healthcare files accepted for processing.");

    public static readonly Counter<long> FilesRejected =
        Meter.CreateCounter<long>("careflow.files.rejected", unit: "{file}", description: "Healthcare files rejected at ingestion.");

    public static readonly Histogram<long> FileSize =
        Meter.CreateHistogram<long>("careflow.files.size", unit: "By", description: "Size of accepted healthcare files.");

    public static readonly Counter<long> FilesParsed =
        Meter.CreateCounter<long>("careflow.files.parsed", unit: "{file}", description: "Files processed by the parser, by format and outcome.");

    public static readonly Counter<long> MessagesConsumed =
        Meter.CreateCounter<long>("careflow.messages.consumed", unit: "{message}", description: "Queue messages handled, by queue and outcome.");

    public static readonly Histogram<double> MessageProcessingDuration =
        Meter.CreateHistogram<double>("careflow.messages.processing.duration", unit: "s", description: "Time spent handling one queue message.");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("careflow.outbox.published", unit: "{event}", description: "Events relayed from the transactional outbox.");

    public static readonly Histogram<double> OutboxLag =
        Meter.CreateHistogram<double>("careflow.outbox.lag", unit: "s", description: "Time from outbox commit to broker publish.");
}
