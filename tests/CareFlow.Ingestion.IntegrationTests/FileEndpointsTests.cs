using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using CareFlow.Ingestion.IntegrationTests.Infrastructure;

namespace CareFlow.Ingestion.IntegrationTests;

[Collection(IngestionApiTests.Name)]
public class FileEndpointsTests(IngestionApiFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("samples/hl7/adt-a01.hl7", "Hl7v2")]
    [InlineData("samples/x12/837p-single-claim.x12", "X12")]
    public async Task Submit_SampleFile_IsStoredEncryptedRecordedAndAnnounced(string samplePath, string expectedFormat)
    {
        using var client = fixture.CreateClientAs("clinic-user", "submitter");

        using var response = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(await File.ReadAllBytesAsync(samplePath), Path.GetFileName(samplePath)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FileResponse>(Json);
        Assert.NotNull(file);
        Assert.Equal(expectedFormat, file.Format);
        Assert.Equal("Received", file.Status);
        Assert.Equal($"/api/v1/files/{file.Id}", response.Headers.Location?.OriginalString);

        // Raw content is in S3, encrypted at rest, and its hash matches.
        var objects = await fixture.S3.ListObjectsV2Async(new() { BucketName = IngestionApiFixture.Bucket });
        var key = Assert.Single(objects.S3Objects, o => o.Key.Contains(file.Id.ToString("N"), StringComparison.Ordinal)).Key;
        var metadata = await fixture.S3.GetObjectMetadataAsync(IngestionApiFixture.Bucket, key);
        Assert.Equal(ServerSideEncryptionMethod.AES256, metadata.ServerSideEncryptionMethod);
        Assert.Equal(file.Sha256, metadata.Metadata["sha256"]);

        // A FileReceived event reached the parser queue through the SNS filter policy.
        var message = await fixture.WaitForMessageAsync(
            fixture.FileReceivedQueueUrl,
            m => m.Body.Contains(file.Id.ToString(), StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));
        Assert.NotNull(message);
        using var body = JsonDocument.Parse(message.Body);
        Assert.Equal(key, body.RootElement.GetProperty("storageKey").GetString());
        Assert.Equal(IngestionApiFixture.Bucket, body.RootElement.GetProperty("bucket").GetString());

        // Status is queryable by a viewer.
        using var viewer = fixture.CreateClientAs("auditor-user", "viewer");
        var fetched = await viewer.GetFromJsonAsync<FileResponse>($"/api/v1/files/{file.Id}", Json);
        Assert.Equal(file.Id, fetched?.Id);
    }

    [Fact]
    public async Task Submit_SameContentTwice_ReturnsOriginalWith200()
    {
        using var client = fixture.CreateClientAs("clinic-user", "submitter");
        var content = UniqueHl7();

        using var first = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(content, "a.hl7"));
        using var second = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(content, "b.hl7"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<FileResponse>(Json);
        var duplicate = await second.Content.ReadFromJsonAsync<FileResponse>(Json);
        Assert.Equal(original!.Id, duplicate!.Id);
    }

    [Fact]
    public async Task Submit_UnsupportedContent_Returns400ProblemWithCode()
    {
        using var client = fixture.CreateClientAs("clinic-user", "submitter");

        using var response = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload("""{"resourceType":"Patient"}"""u8.ToArray(), "patient.json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ingestion.unsupported_format", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_WithoutToken_Returns401()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(UniqueHl7(), "a.hl7"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Submit_AsViewer_Returns403()
    {
        using var client = fixture.CreateClientAs("read-only-user", "viewer");

        using var response = await client.PostAsync(new Uri("/api/v1/files", UriKind.Relative), Upload(UniqueHl7(), "a.hl7"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        using var client = fixture.CreateClientAs("auditor-user", "auditor");

        using var response = await client.GetAsync(new Uri($"/api/v1/files/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessProbe_ReportsHealthyDependencies()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static byte[] UniqueHl7() => Encoding.ASCII.GetBytes(
        $"MSH|^~\\&|TEST|CLINIC|CAREFLOW|CAREFLOW|20260105083000||ADT^A08|{Guid.NewGuid():N}|P|2.5.1\rPID|1||PAT-1\r");

    private sealed record FileResponse(Guid Id, string FileName, string Format, long SizeBytes, string Sha256, string Status);
}
