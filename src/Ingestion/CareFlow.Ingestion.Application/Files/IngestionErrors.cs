using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Ingestion.Application.Files;

public static class IngestionErrors
{
    public static readonly Error EmptyFile =
        Error.Validation("ingestion.empty_file", "The uploaded file is empty.");

    public static readonly Error UnsupportedFormat =
        Error.Validation("ingestion.unsupported_format", "Only HL7 v2 (MSH) and ASC X12 (ISA) content is accepted.");

    public static Error TooLarge(long maxBytes) =>
        Error.Validation("ingestion.too_large", $"The file exceeds the {maxBytes} byte limit.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("ingestion.not_found", $"Ingestion '{id}' was not found.");
}
