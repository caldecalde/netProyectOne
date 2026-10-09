using CareFlow.BuildingBlocks.Observability;
using CareFlow.Parser.Application;
using CareFlow.Parser.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// A worker with a minimal HTTP surface: health probes only, for Kubernetes and docker compose. All work arrives from SQS.
var builder = WebApplication.CreateBuilder(args);

builder.AddCareFlowObservability("parser-worker");

builder.Services.AddParserApplication(builder.Configuration);
builder.Services.AddParserInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(CareFlow.Parser.Infrastructure.DependencyInjection.ReadyTag) });

await app.RunAsync();

/// <summary>Entry point; public for <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;
