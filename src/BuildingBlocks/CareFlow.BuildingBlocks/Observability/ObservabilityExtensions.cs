using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace CareFlow.BuildingBlocks.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Structured JSON logs to stdout (collected by the container runtime / CloudWatch agent) and
    /// OpenTelemetry traces + metrics over OTLP (Jaeger locally, ADOT collector on AWS).
    /// The OTLP exporter is only enabled when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static IHostApplicationBuilder AddCareFlowObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSerilog((_, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var exportOtlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(CareFlowTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(o => o.Filter = IsNotHealthCheck)
                    .AddHttpClientInstrumentation()
                    .AddAWSInstrumentation();
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(CareFlowTelemetry.MeterName)
                    .AddMeter("System.Runtime")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return builder;
    }

    private static bool IsNotHealthCheck(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
