using System.Net.Http.Headers;
using System.Text;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Errors;

namespace AdrMcp.IntegrationTests;

[Collection("azurite")]
public class BlobStoreIntegrationTests : IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _f;
    public BlobStoreIntegrationTests(AzuriteFixture f) => _f = f;

    [AzuriteFact]
    public async Task Upload_grant_allows_exactly_one_blob_write_and_nothing_else()
    {
        var path = $"tenant-it/ws/{Guid.NewGuid():N}/package.json";
        var grant = await _f.Store.CreateSingleBlobWriteGrantAsync(path, "application/json", 1024, TimeSpan.FromMinutes(5), default);
        Assert.Equal("PUT", grant.HttpMethod);
        Assert.Contains("sp=", grant.UploadUri.Query); // permissions present
        Assert.DoesNotContain("sp=r", grant.UploadUri.Query); // no read permission
        Assert.DoesNotContain("sp=l", grant.UploadUri.Query); // no list permission
        Assert.True(grant.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(6));

        using var http = new HttpClient();
        var bytes = Encoding.UTF8.GetBytes("{\"dependencies\":{\"express\":\"4.19.2\"}}");
        using var put = new HttpRequestMessage(HttpMethod.Put, grant.UploadUri) { Content = new ByteArrayContent(bytes) };
        foreach (var h in grant.RequiredHeaders) if (h.Key != "Content-Type") put.Headers.TryAddWithoutValidation(h.Key, h.Value);
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var resp = await http.SendAsync(put);
        Assert.True(resp.IsSuccessStatusCode, $"PUT failed: {(int)resp.StatusCode}");

        // The same SAS cannot read the blob back or list the container.
        var read = await http.GetAsync(grant.UploadUri);
        Assert.False(read.IsSuccessStatusCode);
        var listUri = new UriBuilder(grant.UploadUri) { Path = grant.UploadUri.AbsolutePath[..grant.UploadUri.AbsolutePath.IndexOf('/', 1)], Query = grant.UploadUri.Query.TrimStart('?') + "&restype=container&comp=list" }.Uri;
        var list = await http.GetAsync(listUri);
        Assert.False(list.IsSuccessStatusCode);

        // A different blob name with the same SAS is rejected (single-object scope).
        var other = new UriBuilder(grant.UploadUri) { Path = grant.UploadUri.AbsolutePath.Replace("package.json", "other.json") }.Uri;
        using var otherPut = new HttpRequestMessage(HttpMethod.Put, other) { Content = new ByteArrayContent(bytes) };
        otherPut.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        Assert.False((await http.SendAsync(otherPut)).IsSuccessStatusCode);

        var props = await _f.Store.GetPropertiesAsync(EvidenceContainer.Quarantine, path, default);
        Assert.True(props.Exists);
        Assert.Equal(bytes.Length, props.Length);
        var (sha, len) = await _f.Store.ComputeSha256Async(EvidenceContainer.Quarantine, path, 10_000, default);
        Assert.Equal(Hashing.Sha256Hex(bytes), sha);
        Assert.Equal(bytes.Length, len);

        await _f.Store.CopyAsync(EvidenceContainer.Quarantine, path, EvidenceContainer.Approved, path, default);
        await _f.Store.DeleteIfExistsAsync(EvidenceContainer.Quarantine, path, default);
        Assert.False((await _f.Store.GetPropertiesAsync(EvidenceContainer.Quarantine, path, default)).Exists);
        Assert.Equal("{\"dependencies\":{\"express\":\"4.19.2\"}}", await _f.Store.ReadTextAsync(EvidenceContainer.Approved, path, 10_000, default));
    }

    [AzuriteFact]
    public async Task Hash_computation_enforces_byte_cap()
    {
        var path = $"tenant-it/ws/{Guid.NewGuid():N}/big.txt";
        await _f.Store.WriteTextAsync(EvidenceContainer.Quarantine, path, new string('x', 5000), "text/plain", null, default);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _f.Store.ComputeSha256Async(EvidenceContainer.Quarantine, path, 1000, default));
        Assert.Equal(ErrorCodes.PayloadTooLarge, ex.Code);
    }

    [Theory]
    [InlineData("tenant/ws/../x")]
    [InlineData("/tenant/ws/x")]
    [InlineData("tenant//ws/x")]
    [InlineData("flat.txt")]
    public void Paths_outside_tenant_scope_are_rejected(string path) => Assert.Throws<ValidationException>(() => AdrMcp.Infrastructure.Blob.BlobEvidenceStore.ValidatePath(path));
}
