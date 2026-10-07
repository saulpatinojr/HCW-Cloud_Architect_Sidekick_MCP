namespace AdrMcp.Application.Common;

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    public static string Adr(int number) => $"adr-{number:D4}";
    public static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
}
