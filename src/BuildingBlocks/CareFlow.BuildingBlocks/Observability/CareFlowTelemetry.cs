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
}
