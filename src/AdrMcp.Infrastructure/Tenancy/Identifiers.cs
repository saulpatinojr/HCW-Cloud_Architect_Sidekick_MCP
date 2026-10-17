namespace AdrMcp.Infrastructure.Tenancy;

/// <summary>The one rule for tenant and workspace identifiers, which become partition keys and blob path segments.</summary>
internal static class Identifiers
{
    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
