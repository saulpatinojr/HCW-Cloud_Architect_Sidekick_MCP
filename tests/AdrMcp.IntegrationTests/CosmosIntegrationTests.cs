using AdrMcp.Application.Common;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Infrastructure.Cosmos;
using AdrMcp.Infrastructure.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AdrMcp.IntegrationTests;

/// <summary>Runs only when a Cosmos endpoint/emulator is configured. Exercises ETag conflicts and partition isolation against the real SDK.</summary>
public class CosmosIntegrationTests
{
    private static CosmosContext Create()
    {
        var cs = Environment.GetEnvironmentVariable("ADR_TEST_COSMOS_CONNECTION_STRING");
        var endpoint = Environment.GetEnvironmentVariable("ADR_TEST_COSMOS_ENDPOINT");
        var options = new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = AppJson.Options, ConnectionMode = ConnectionMode.Gateway };
        var client = cs is not null ? new CosmosClient(cs, options) : new CosmosClient(endpoint!, new Azure.Identity.DefaultAzureCredential(), options);
        var db = client.CreateDatabaseIfNotExistsAsync("adr-it").GetAwaiter().GetResult().Database;
        db.CreateContainerIfNotExistsAsync("adrs", "/partitionKey").GetAwaiter().GetResult();
        return new CosmosContext(client, Options.Create(new CosmosOptions { DatabaseName = "adr-it" }));
    }

    private static Adr NewAdr(string tenant, string ws, int n) => new()
    {
        Id = $"adr-{n:D4}-{Guid.NewGuid():N}", TenantId = tenant, WorkspaceId = ws, AdrNumber = n,
        Human = new HumanOwnedContent { Title = $"ADR {n}", Decision = "d", Status = AdrStatus.Accepted, Tags = new[] { "it" } },
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Provenance = Provenance.Human("it", DateTimeOffset.UtcNow, "c"), ContentHash = "h"
    };

    [CosmosFact]
    public async Task Etag_conflict_and_partition_isolation()
    {
        var repo = new CosmosAdrRepository(Create());
        var adr = await repo.CreateAsync(NewAdr("t1", "w1", 1), default);
        Assert.NotNull(adr.ETag);
        var copy = await repo.GetAsync("t1", "w1", adr.Id, default);
        adr.Human = adr.Human with { Title = "first" }; await repo.ReplaceAsync(adr, default);
        copy!.Human = copy.Human with { Title = "second" };
        await Assert.ThrowsAsync<ConcurrencyException>(() => repo.ReplaceAsync(copy, default));
        Assert.Null(await repo.GetAsync("t2", "w1", adr.Id, default));
        Assert.Null(await repo.GetAsync("t1", "w2", adr.Id, default));
        var listed = await repo.ListByStatusesAsync("t1", "w1", new[] { AdrStatus.Accepted }, default);
        Assert.Contains(listed, a => a.Id == adr.Id);
        Assert.True(await repo.NextAdrNumberAsync("t1", "w1", default) >= 2);
    }
}
