using AdrMcp.Application.Options;
using AdrMcp.Functions.Telemetry;
using AdrMcp.Infrastructure;
using AdrMcp.Infrastructure.Options;
using System.Security.Cryptography;
using AdrMcp.Functions.Http;
using AdrMcp.Infrastructure.Tenancy;
using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AdrMcp.UnitTests.Infrastructure;

public class PrincipalHashSaltValidationTests
{
    private static readonly string GeneratedSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); // same as `openssl rand -base64 48`

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(PrincipalHashSaltValidator.DefaultPlaceholder)]
    [InlineData("@Microsoft.KeyVault(VaultName=kv-adr-dev-scus-01;SecretName=principal-hash-salt)")]   // an unresolved reference as App Service leaves it; the vault is the one infra/main.bicep builds (ADR-0009)
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

    [Theory]
    [InlineData("Cosmos")]
    [InlineData(null)]   // Persistence:Provider unset defaults to Cosmos
    public async Task Host_start_fails_fast_with_durable_persistence_and_the_default_salt(string? provider)
    {
        using var host = BuildHost(new() { ["Persistence:Provider"] = provider });
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

public class EntraReadinessTests
{
    [Theory]
    [InlineData("FunctionKey", false, 200)]
    [InlineData("EntraBuiltIn", true, 200)]
    [InlineData("EntraBuiltIn", false, 503)]   // would reject every call: fail the deploy job's health wait instead
    public void Health_reports_unavailable_when_entra_mode_lacks_platform_authentication(string mode, bool platformAuth, int expected)
    {
        var health = new HealthFunction(Options.Create(new AuthOptions { Mode = mode }), new PlatformAuthentication(platformAuth));
        var result = Assert.IsAssignableFrom<ObjectResult>(health.Run(new DefaultHttpContext().Request));
        Assert.Equal(expected, result.StatusCode ?? StatusCodes.Status200OK);
    }

    [Fact]
    public void Health_function_activates_from_the_app_composition()
    {
        // The isolated worker creates function classes with ActivatorUtilities from the app's service provider.
        var health = ActivatorUtilities.CreateInstance<HealthFunction>(new AdrMcp.UnitTests.Support.TestHost().Services);
        Assert.IsType<OkObjectResult>(health.Run(new DefaultHttpContext().Request));
    }

    [Fact]
    public void Infrastructure_never_sets_the_platform_authentication_flag()
    {
        // WEBSITE_AUTH_ENABLED must come from the platform. Setting it as an app setting would let Entra mode trust a forged
        // X-MS-CLIENT-PRINCIPAL header while App Service Authentication is off.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "AdrMcp.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var offenders = Directory.EnumerateFiles(Path.Join(root.FullName, "infra"), "*.*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(PlatformAuthentication.EnabledSetting, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(offenders);
    }
}
