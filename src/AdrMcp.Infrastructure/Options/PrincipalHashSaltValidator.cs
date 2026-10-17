using AdrMcp.Application.Options;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Options;

/// <summary>
/// Fails startup when durable persistence would store principal hashes made with a guessable salt: the built-in default,
/// an App Service Key Vault reference that did not resolve (the platform then passes the literal "@Microsoft.KeyVault(...)"
/// string), or a value too short to be a generated secret. InMemory persistence (local development and tests) stores
/// nothing durable, so any salt is accepted there. Messages never include the configured value.
/// </summary>
public sealed class PrincipalHashSaltValidator : IValidateOptions<AdrOptions>
{
    public const int MinimumLength = 32;
    public const string DefaultPlaceholder = "change-me-via-keyvault";
    private const string UnresolvedKeyVaultReferencePrefix = "@Microsoft.KeyVault(";
    private const string Remedy = "Create the Key Vault secret 'principal-hash-salt' (scripts/postprovision.sh does this), check that the app identity can read it, then restart the app.";

    private readonly bool _requireConfiguredSalt;

    public PrincipalHashSaltValidator(bool requireConfiguredSalt) => _requireConfiguredSalt = requireConfiguredSalt;

    public ValidateOptionsResult Validate(string? name, AdrOptions options)
    {
        if (!_requireConfiguredSalt) return ValidateOptionsResult.Success;

        var salt = options.PrincipalHashSalt?.Trim() ?? string.Empty;
        if (salt.StartsWith(UnresolvedKeyVaultReferencePrefix, StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail($"Adr:PrincipalHashSalt is an unresolved Key Vault reference. {Remedy}");
        if (salt.Length == 0 || string.Equals(salt, DefaultPlaceholder, StringComparison.Ordinal))
            return ValidateOptionsResult.Fail($"Adr:PrincipalHashSalt is not configured. {Remedy}");
        if (salt.Length < MinimumLength)
            return ValidateOptionsResult.Fail($"Adr:PrincipalHashSalt must be at least {MinimumLength} characters. {Remedy}");
        return ValidateOptionsResult.Success;
    }
}
