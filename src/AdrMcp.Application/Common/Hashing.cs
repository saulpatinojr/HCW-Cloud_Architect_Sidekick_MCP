using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdrMcp.Application.Common;

public static class Hashing
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Hash of the canonical JSON form (property order as declared, camelCase). Used for ContentHash and idempotency.</summary>
    public static string CanonicalHash<T>(T value) => Sha256Hex(JsonSerializer.Serialize(value, AppJson.Canonical));

    /// <summary>Short, salted, non-reversible identifier for principals in audit/memory records.</summary>
    public static string PrincipalHash(string principalId, string salt) => Sha256Hex($"{salt}:{principalId}")[..32];

    /// <summary>Short fingerprint of a secret for de-duplicating findings without storing the secret.</summary>
    public static string Fingerprint(string value) => Sha256Hex(value)[..12];
}
