using System.Security.Cryptography;
using System.Text;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Errors;
using AdrMcp.Infrastructure.Options;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Blob;

/// <summary>
/// Blob storage adapter. Upload grants are user-delegation SAS tokens (identity-based, no account key) scoped to one blob with
/// Create+Write only and a short expiry. With Azurite (shared-key connection string) the same builder signs with the account key,
/// which is acceptable for local development only.
/// </summary>
public sealed class BlobEvidenceStore : IEvidenceBlobStore
{
    private readonly BlobServiceClient _client;
    private readonly StorageOptions _options;
    private readonly ILogger<BlobEvidenceStore> _logger;

    public BlobEvidenceStore(BlobServiceClient client, IOptions<StorageOptions> options, ILogger<BlobEvidenceStore> logger)
    {
        _client = client; _options = options.Value; _logger = logger;
    }

    public string ContainerName(EvidenceContainer c) => c switch
    {
        EvidenceContainer.Quarantine => _options.QuarantineContainer,
        EvidenceContainer.Approved => _options.ApprovedContainer,
        EvidenceContainer.Sanitized => _options.SanitizedContainer,
        EvidenceContainer.AdrSource => _options.AdrSourceContainer,
        EvidenceContainer.Reports => _options.ReportsContainer,
        EvidenceContainer.Snapshots => _options.SnapshotsContainer,
        EvidenceContainer.Manifests => _options.ManifestsContainer,
        EvidenceContainer.Diagrams => _options.DiagramsContainer,
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _ensured = new();

    private BlobClient Client(EvidenceContainer container, string path)
    {
        var containerClient = _client.GetBlobContainerClient(ContainerName(container));
        // In Azure the Bicep template creates containers; locally (Azurite) create on first use. One round-trip per container per process.
        if (_ensured.TryAdd(containerClient.Name, true))
        {
            try { containerClient.CreateIfNotExists(PublicAccessType.None); }
            catch (RequestFailedException ex) when (ex.Status is 403 or 409) { /* identity lacks create rights or container already exists */ }
        }
        return containerClient.GetBlobClient(ValidatePath(path));
    }

    /// <summary>Paths must be tenant-scoped relative paths with no traversal, absolute roots or control characters.</summary>
    public static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) throw new ValidationException("Blob path is invalid.");
        if (path.StartsWith('/') || path.Contains("..") || path.Contains('\\') || path.Any(char.IsControl) || path.Contains("//")) throw new ValidationException("Blob path contains illegal segments.");
        if (path.Split('/').Length < 3) throw new ValidationException("Blob path must be tenant/workspace scoped.");
        return path;
    }

    public async Task<UploadGrant> CreateSingleBlobWriteGrantAsync(string quarantineBlobPath, string contentType, long maxBytes, TimeSpan ttl, CancellationToken ct)
    {
        var blob = Client(EvidenceContainer.Quarantine, quarantineBlobPath);
        var now = DateTimeOffset.UtcNow;
        var builder = new BlobSasBuilder
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = now.Add(ttl),
            Protocol = SasProtocol.Https,
            ContentType = contentType,
        };
        builder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        Uri uri;
        if (blob.CanGenerateSasUri)
        {
            // Shared-key path: local Azurite / development only.
            if (!IsLocalEmulator(blob.Uri)) _logger.LogWarning("Shared-key SAS generation is enabled against a non-emulator endpoint; prefer managed identity + user delegation.");
            builder.Protocol = IsLocalEmulator(blob.Uri) ? SasProtocol.HttpsAndHttp : SasProtocol.Https;
            uri = blob.GenerateSasUri(builder);
        }
        else
        {
            // Identity-based path: requires the 'Storage Blob Delegator' role on the account for the function's managed identity.
            var key = await _client.GetUserDelegationKeyAsync(builder.StartsOn, builder.ExpiresOn, ct);
            var sas = builder.ToSasQueryParameters(key.Value, _client.AccountName);
            uri = new UriBuilder(blob.Uri) { Query = sas.ToString() }.Uri;
        }
        var headers = new Dictionary<string, string>
        {
            ["x-ms-blob-type"] = "BlockBlob",
            ["Content-Type"] = contentType,
            ["x-ms-version"] = "2024-11-04",
        };
        return new UploadGrant(uri, "PUT", headers, builder.ExpiresOn);
    }

    private static bool IsLocalEmulator(Uri uri) => uri.Host is "127.0.0.1" or "localhost" or "azurite" || uri.Port == 10000;

    public async Task<Application.Abstractions.BlobProperties> GetPropertiesAsync(EvidenceContainer container, string blobPath, CancellationToken ct)
    {
        var blob = Client(container, blobPath);
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            IReadOnlyDictionary<string, string> tags = new Dictionary<string, string>();
            try { var t = await blob.GetTagsAsync(cancellationToken: ct); tags = new Dictionary<string, string>(t.Value.Tags); } catch (RequestFailedException) { /* tags permission may be absent; Defender verdict unavailable */ }
            return new Application.Abstractions.BlobProperties(true, props.Value.ContentLength, props.Value.ContentType, props.Value.ETag.ToString(), props.Value.LastModified, tags);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new Application.Abstractions.BlobProperties(false, 0, null, null, null, new Dictionary<string, string>());
        }
    }

    public async Task<(string Sha256, long Bytes)> ComputeSha256Async(EvidenceContainer container, string blobPath, long maxBytes, CancellationToken ct)
    {
        var blob = Client(container, blobPath);
        await using var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(false) { BufferSize = 1024 * 1024 }, ct);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 256];
        long total = 0; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new DomainException(ErrorCodes.PayloadTooLarge, $"Blob exceeds {maxBytes} bytes.");
            sha.AppendData(buffer, 0, read);
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total);
    }

    public async Task<string> ReadTextAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(container, blobPath, maxBytes, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]> ReadBytesAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct)
    {
        var blob = Client(container, blobPath);
        await using var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(false), ct);
        using var ms = new MemoryStream();
        var buffer = new byte[1024 * 64];
        int read;
        while (ms.Length < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - ms.Length)), ct)) > 0) ms.Write(buffer, 0, read);
        return ms.ToArray();
    }

    public async Task WriteTextAsync(EvidenceContainer container, string blobPath, string text, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
    {
        var blob = Client(container, blobPath);
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType }, Metadata = metadata is null ? null : new Dictionary<string, string>(metadata) };
        await blob.UploadAsync(BinaryData.FromString(text), options, ct);
    }

    public async Task CopyAsync(EvidenceContainer sourceContainer, string sourcePath, EvidenceContainer targetContainer, string targetPath, CancellationToken ct)
    {
        var source = Client(sourceContainer, sourcePath);
        var target = Client(targetContainer, targetPath);
        // Server-side copy within the same account works with the caller's identity; no SAS needed.
        var op = await target.StartCopyFromUriAsync(source.Uri, cancellationToken: ct);
        await op.WaitForCompletionAsync(ct);
    }

    public async Task DeleteIfExistsAsync(EvidenceContainer container, string blobPath, CancellationToken ct)
    {
        await Client(container, blobPath).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    /// <summary>Creates containers when they do not exist (development convenience; in Azure the Bicep template creates them).</summary>
    public async Task EnsureContainersAsync(CancellationToken ct)
    {
        foreach (EvidenceContainer c in Enum.GetValues<EvidenceContainer>())
            await _client.GetBlobContainerClient(ContainerName(c)).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    }
}
