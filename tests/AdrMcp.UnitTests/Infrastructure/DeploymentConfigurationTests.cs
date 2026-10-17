using AdrMcp.Application.Options;
using AdrMcp.Functions.Telemetry;
using AdrMcp.Infrastructure;
using AdrMcp.Infrastructure.Options;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AdrMcp.UnitTests.Infrastructure;

public class PrincipalHashSaltValidationTests
{
    private const string GeneratedSalt = "q3Zp1vJ0n8mXc2Lr7TfYw4Bd9Hs6Ka5UeGo0NiRtVb1MxCzQy8WjPl2Fh4Ds7Ag3"; // shape of `openssl rand -base64 48`

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(PrincipalHashSaltValidator.DefaultPlaceholder)]
    [InlineData("@Microsoft.KeyVault(VaultName=kv-adr;SecretName=principal-hash-salt)")]
    [InlineData("short-but-not-default")]
    public void Durable_persistence_rejects_placeholder_unresolved_or_weak_salts(string salt)
    {
        var result = new PrincipalHashSaltValidator(requireConfiguredSalt: true).Validate(null, new AdrOptions { PrincipalHashSalt = salt });

        Assert.True(result.Failed);
        Assert.Contains("principal-hash-salt", result.FailureMessage);
        if (salt.Trim().Length > 0) Assert.DoesNotContain(salt.Trim(), result.FailureMessage);   // never echo the configured value
    }

    [Fact]
    public void Durable_persistence_accepts_a_generated_salt()
        => Assert.True(new PrincipalHashSaltValidator(requireConfiguredSalt: true).Validate(null, new AdrOptions { PrincipalHashSalt = GeneratedSalt }).Succeeded);

    [Fact]
    public void InMemory_persistence_accepts_any_salt()
        => Assert.True(new PrincipalHashSaltValidator(requireConfiguredSalt: false).Validate(null, new AdrOptions()).Succeeded);

    [Fact]
    public async Task Host_start_fails_fast_with_cosmos_persistence_and_the_default_salt()
    {
        using var host = BuildHost(new() { ["Persistence:Provider"] = "Cosmos" });
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Adr:PrincipalHashSalt", ex.Message);
    }

    [Fact]
    public async Task Host_starts_with_cosmos_persistence_and_a_generated_salt()
    {
        using var host = BuildHost(new() { ["Persistence:Provider"] = "Cosmos", ["Adr:PrincipalHashSalt"] = GeneratedSalt });
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Host_starts_with_inmemory_persistence_and_the_default_salt()
    {
        using var host = BuildHost(new() { ["Persistence:Provider"] = "InMemory" });
        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost BuildHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        builder.Services.AddAdrApplication(config);
        builder.Services.AddAdrPersistence(config);
        return builder.Build();
    }
}

public class ApplicationInsightsAuthenticationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Authorization=None")]
    [InlineData("ClientId=00000000-0000-0000-0000-000000000000")]
    [InlineData("not a connection string")]
    public void No_credential_unless_entra_authorization_is_requested(string? value)
    {
        Assert.False(ApplicationInsightsAuthentication.TryCreateCredential(value, out var credential));
        Assert.Null(credential);
    }

    [Theory]
    [InlineData("Authorization=AAD")]
    [InlineData("ClientId=11111111-2222-3333-4444-555555555555;Authorization=AAD")]   // format written by infra/modules/functionapp.bicep
    [InlineData(" authorization = aad ; clientid = 11111111-2222-3333-4444-555555555555 ")]
    public void Entra_authorization_yields_a_managed_identity_credential(string value)
    {
        Assert.True(ApplicationInsightsAuthentication.TryCreateCredential(value, out var credential));
        Assert.IsType<ManagedIdentityCredential>(credential);
    }
}
