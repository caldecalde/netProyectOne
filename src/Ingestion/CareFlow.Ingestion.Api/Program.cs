using System.Security.Claims;
using System.Threading.RateLimiting;
using CareFlow.BuildingBlocks.Observability;
using CareFlow.BuildingBlocks.Security;
using CareFlow.Ingestion.Api.Endpoints;
using CareFlow.Ingestion.Application;
using CareFlow.Ingestion.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddCareFlowObservability("ingestion-api");

builder.Services.AddIngestionApplication(builder.Configuration);
builder.Services.AddIngestionInfrastructure(builder.Configuration);
builder.Services.AddCareFlowAuth(builder.Configuration);
builder.Services.AddProblemDetails();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(FileEndpoints.UploadRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(CareFlowClaims.Subject) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:UploadsPerMinute", 30),
            Window = TimeSpan.FromMinutes(1),
        }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new OpenApiInfo { Title = "CareFlow Ingestion API", Version = "v1" });
    var bearer = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    swagger.AddSecurityDefinition("Bearer", bearer);
    swagger.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.CacheControl = "no-store";
    await next(context);
});

app.UseSerilogRequestLogging();

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapFileEndpoints(app.Configuration.GetValue("Ingestion:MaxFileBytes", 10L * 1024 * 1024));

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(CareFlow.Ingestion.Infrastructure.DependencyInjection.ReadyTag) });

await app.RunAsync();

/// <summary>Entry point; public for <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;
