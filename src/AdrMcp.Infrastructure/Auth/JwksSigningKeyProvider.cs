using AdrMcp.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace AdrMcp.Infrastructure.Auth;

public enum KeyLookupStatus { Found, NotFound, Unavailable }

public readonly record struct SigningKeyLookup(KeyLookupStatus Status, SecurityKey? Key)
{
    public static SigningKeyLookup Found(SecurityKey key) => new(KeyLookupStatus.Found, key);
    public static readonly SigningKeyLookup NotFound = new(KeyLookupStatus.NotFound, null);
    public static readonly SigningKeyLookup Unavailable = new(KeyLookupStatus.Unavailable, null);
}

public interface ISigningKeyProvider
{
    /// <summary>Finds the public key with this <c>kid</c>. <see cref="KeyLookupStatus.Unavailable"/> means "could not decide", never "accept".</summary>
    Task<SigningKeyLookup> GetKeyAsync(string keyId, CancellationToken ct);
}

/// <summary>
/// Fetches and caches the tenant's public signing keys (a JWKS document). Rules, all in ADR-0008:
///  - keys are used for at most <c>ttl</c>; after that they are fetched again, and if that fails the answer is Unavailable
///    (no stale keys, no "accept"), which callers turn into 503;
///  - a <c>kid</c> that is not cached triggers a refresh, but at most one per <see cref="UnknownKeyRefreshInterval"/> per instance,
///    so a caller sending random key IDs cannot make the app hammer the key endpoint;
///  - a failed fetch is not retried for <see cref="FailureBackoff"/>, so an outage does not become a request storm;
///  - one fetch at a time (single flight); the others wait for its result;
///  - only RSA signing keys of at least 2048 bits with a <c>kid</c> are kept, and keys bound to another issuer are dropped;
///  - the response is capped at <see cref="MaxDocumentBytes"/>, redirects are not followed, and logs carry a fixed reason only.
/// </summary>
public sealed class JwksSigningKeyProvider : ISigningKeyProvider, IDisposable
{
    public static readonly TimeSpan UnknownKeyRefreshInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    public const int MaxDocumentBytes = 256 * 1024;
    private const int MinRsaModulusBytes = 256;   // 2048 bits

    private sealed record Snapshot(IReadOnlyDictionary<string, SecurityKey> Keys, DateTimeOffset FetchedAt);

    private readonly EntraTokenPolicy _policy;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly IClock _clock;
    private readonly ILogger<JwksSigningKeyProvider> _logger;
    private readonly TimeSpan _fetchTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _snapshot;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;   // guarded by _gate
    private bool _lastAttemptFailed;                                 // guarded by _gate

    /// <summary>Production constructor: its own HttpClient with no redirects and a short connection lifetime so DNS changes are noticed.</summary>
    public JwksSigningKeyProvider(EntraTokenPolicy policy, IClock clock, ILogger<JwksSigningKeyProvider> logger)
        : this(policy, new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = FetchTimeout,
        }) { Timeout = FetchTimeout + TimeSpan.FromSeconds(2) }, clock, logger, ownsClient: true)
    {
    }

    /// <summary>For tests: the caller supplies the HttpClient (and so the handler that answers the key request).</summary>
    public JwksSigningKeyProvider(EntraTokenPolicy policy, HttpClient http, IClock clock, ILogger<JwksSigningKeyProvider> logger, bool ownsClient = false, TimeSpan? fetchTimeout = null)
    {
        _policy = policy; _http = http; _clock = clock; _logger = logger; _ownsClient = ownsClient; _fetchTimeout = fetchTimeout ?? FetchTimeout;
    }

    public async Task<SigningKeyLookup> GetKeyAsync(string keyId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(keyId)) return SigningKeyLookup.NotFound;
        var snapshot = _snapshot;
        var now = _clock.UtcNow;
        var fresh = snapshot is not null && now - snapshot.FetchedAt < _policy.JwksCacheTtl;
        if (fresh && snapshot!.Keys.TryGetValue(keyId, out var cached)) return SigningKeyLookup.Found(cached);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited for the gate.
            snapshot = _snapshot;
            now = _clock.UtcNow;
            fresh = snapshot is not null && now - snapshot.FetchedAt < _policy.JwksCacheTtl;
            if (fresh && snapshot!.Keys.TryGetValue(keyId, out cached)) return SigningKeyLookup.Found(cached);

            if (fresh && now - _lastAttempt < UnknownKeyRefreshInterval)
                // Keys are current and a refresh ran a moment ago. If it worked, this kid is simply unknown (401); if it failed,
                // the token may be signed by a key that could not be fetched (503).
                return _lastAttemptFailed ? SigningKeyLookup.Unavailable : SigningKeyLookup.NotFound;
            if (_lastAttemptFailed && now - _lastAttempt < FailureBackoff)
                return SigningKeyLookup.Unavailable;         // the last fetch failed a moment ago: do not try again yet

            _lastAttempt = now;
            var fetched = await FetchAsync(ct).ConfigureAwait(false);
            _lastAttemptFailed = fetched is null;
            if (fetched is null)
            {
                // Expired keys are dropped from use: without a working fetch the app cannot tell a good token from a forged one.
                if (!fresh) _snapshot = null;
                return SigningKeyLookup.Unavailable;
            }
            _snapshot = new Snapshot(fetched, _clock.UtcNow);
            return fetched.TryGetValue(keyId, out var key) ? SigningKeyLookup.Found(key) : SigningKeyLookup.NotFound;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, SecurityKey>?> FetchAsync(CancellationToken ct)
    {
        string reason;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_fetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, _policy.JwksUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { reason = "http_error"; }
            else if (response.Content.Headers.ContentLength > MaxDocumentBytes) { reason = "too_large"; }
            else
            {
                var json = await ReadBoundedAsync(response, cts.Token).ConfigureAwait(false);
                if (json is null) { reason = "too_large"; }
                else
                {
                    var keys = ParseKeys(json);
                    if (keys is not null) { _logger.LogInformation("Signing keys refreshed ({Count} keys)", keys.Count); return keys; }
                    reason = "no_usable_keys";
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = "timeout"; }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { reason = "network"; }
        catch (Exception) { reason = "invalid_document"; }   // deliberately not logged: library messages can echo document content
        _logger.LogWarning("Signing key refresh failed ({Reason})", reason);
        return null;
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxDocumentBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total > MaxDocumentBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private IReadOnlyDictionary<string, SecurityKey>? ParseKeys(string json)
    {
        var set = new JsonWebKeySet(json);
        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var jwk in set.Keys)
        {
            if (!string.Equals(jwk.Kty, "RSA", StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(jwk.Use) && !string.Equals(jwk.Use, "sig", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(jwk.Kid) || string.IsNullOrEmpty(jwk.N) || string.IsNullOrEmpty(jwk.E)) continue;
            if (Base64UrlEncoder.DecodeBytes(jwk.N).Length < MinRsaModulusBytes) continue;
            if (BoundToAnotherIssuer(jwk)) continue;
            keys[jwk.Kid] = jwk;
        }
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>Entra's key documents can tag a key with the issuer that uses it (a GUID issuer or a {tenantid} template); a key for another issuer is not ours.</summary>
    private bool BoundToAnotherIssuer(JsonWebKey jwk)
    {
        if (!jwk.AdditionalData.TryGetValue("issuer", out var value) || value is not string issuer || issuer.Length == 0) return false;
        issuer = issuer.Replace("{tenantid}", _policy.TenantId, StringComparison.OrdinalIgnoreCase);
        return !string.Equals(issuer, _policy.Issuer, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsClient) _http.Dispose();
    }
}
