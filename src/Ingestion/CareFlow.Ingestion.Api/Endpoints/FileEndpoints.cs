using System.Security.Claims;
using CareFlow.BuildingBlocks.Security;
using CareFlow.Ingestion.Application.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CareFlow.Ingestion.Api.Endpoints;

internal static class FileEndpoints
{
    public const string UploadRateLimitPolicy = "uploads";

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app, long maxFileBytes)
    {
        var files = app.MapGroup("/api/v1/files").WithTags("Files");

        files.MapPost("/", SubmitAsync)
            .WithName("SubmitFile")
            .WithSummary("Submit an HL7 v2 or X12 file for processing.")
            .RequireAuthorization(CareFlowPolicies.CanSubmit)
            .RequireRateLimiting(UploadRateLimitPolicy)
            // Bearer-token API: no cookies, so no CSRF surface.
            .DisableAntiforgery()
            // Leave headroom for multipart boundaries; the handler enforces the exact content limit.
            .WithMetadata(new RequestSizeLimitAttribute(maxFileBytes + (64 * 1024)))
            .ProducesValidationProblem();

        files.MapGet("/{id:guid}", GetAsync)
            .WithName("GetFile")
            .WithSummary("Get the processing status of a submitted file.")
            .RequireAuthorization(CareFlowPolicies.CanRead);

        return app;
    }

    private static async Task<Results<Accepted<IngestionFileDto>, Ok<IngestionFileDto>, ProblemHttpResult>> SubmitAsync(
        IFormFile file,
        ClaimsPrincipal user,
        SubmitFileHandler handler,
        CancellationToken cancellationToken)
    {
        var subject = user.FindFirstValue(CareFlowClaims.Subject)
            ?? throw new InvalidOperationException("Authenticated principal has no subject claim.");

        await using var content = file.OpenReadStream();
        var result = await handler.HandleAsync(new SubmitFileCommand(file.FileName, content, subject), cancellationToken);

        return result.Match<Results<Accepted<IngestionFileDto>, Ok<IngestionFileDto>, ProblemHttpResult>>(
            ok => ok.IsDuplicate
                ? TypedResults.Ok(ok.File)
                : TypedResults.Accepted($"/api/v1/files/{ok.File.Id}", ok.File),
            error => ProblemResults.From(error));
    }

    private static async Task<Results<Ok<IngestionFileDto>, ProblemHttpResult>> GetAsync(
        Guid id,
        GetFileHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.Match<Results<Ok<IngestionFileDto>, ProblemHttpResult>>(
            dto => TypedResults.Ok(dto),
            error => ProblemResults.From(error));
    }
}
