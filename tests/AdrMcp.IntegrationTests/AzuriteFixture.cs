using System.Net.Sockets;
using AdrMcp.Infrastructure.Blob;
using AdrMcp.Infrastructure.Options;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdrMcp.IntegrationTests;

/// <summary>
/// Uses a running Azurite instance (default ports). Tests are skipped when Azurite is not reachable so the suite stays green
/// on machines without the emulator; CI starts Azurite as a service container.
/// </summary>
public sealed class AzuriteFixture
{
    public const string ConnectionString = "UseDevelopmentStorage=true";
    public bool Available { get; }
    public BlobServiceClient Client { get; }
    public BlobEvidenceStore Store { get; }

    public AzuriteFixture()
    {
        Available = PortOpen("127.0.0.1", 10000) && PortOpen("127.0.0.1", 10001);
        Client = new BlobServiceClient(ConnectionString);
        Store = new BlobEvidenceStore(Client, Options.Create(new StorageOptions { ConnectionString = ConnectionString, QuarantineContainer = "it-quarantine", ApprovedContainer = "it-evidence", SanitizedContainer = "it-sanitized", AdrSourceContainer = "it-adr-source" }), NullLogger<BlobEvidenceStore>.Instance);
    }

    private static bool PortOpen(string host, int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync(host, port).Wait(500); } catch { return false; }
    }
}

/// <summary>xunit skip helper: facts marked with this attribute are skipped when Azurite is down.</summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!new AzuriteFixture().Available) Skip = "Azurite is not running on 127.0.0.1:10000/10001 (start with `azurite --silent`).";
    }
}

public sealed class CosmosFactAttribute : FactAttribute
{
    public CosmosFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADR_TEST_COSMOS_ENDPOINT")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADR_TEST_COSMOS_CONNECTION_STRING")))
            Skip = "Set ADR_TEST_COSMOS_ENDPOINT (identity) or ADR_TEST_COSMOS_CONNECTION_STRING (emulator) to run Cosmos DB integration tests.";
    }
}
