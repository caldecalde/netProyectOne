using CareFlow.BuildingBlocks.Results;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CareFlow.Ingestion.Api.Endpoints;

internal static class ProblemResults
{
    /// <summary>Maps application errors to RFC 9457 problem details. Messages are safe to show clients (no PHI).</summary>
    public static ProblemHttpResult From(Error error) => TypedResults.Problem(
        title: error.Message,
        statusCode: error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        },
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
