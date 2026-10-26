using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdrMcp.UnitTests.Auth;

/// <summary>Time that only moves when a test says so.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Answers the key request from memory, counts calls, and can be told to fail. No network.</summary>
public sealed class StubJwksHandler : HttpMessageHandler
{
    private readonly Func<string> _document;
    public int Requests;
    public Func<HttpRequestMessage, HttpResponseMessage>? Override { get; set; }
    public bool Fail { get; set; }
    public TimeSpan Delay { get; set; }

    public StubJwksHandler(Func<string> document) => _document = document;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Fail) throw new HttpRequestException("stubbed outage");
        if (Override is not null) return Override(request);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_document(), Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// A stand-in for Entra that exists only inside a test: one RSA key pair generated at test time, a matching key set document, and a
/// token minter that can produce valid tokens and every kind of broken one. No token or key is ever committed.
/// </summary>
public sealed class TestIdp : IDisposable
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string OtherTenantId = "22222222-2222-2222-2222-222222222222";
    public const string ServerClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string AllowedClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    public const string OtherClientId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    public const string ObjectId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    public const string UserName = "owner@contoso.example";
    public const string ResourceUri = "https://func-adr-test.azurewebsites.net/runtime/webhooks/mcp";
    public const string KeyId = "kid-current";

    public RSA Key { get; } = RSA.Create(2048);
    public FakeClock Clock { get; } = new();

    public static string Issuer(string tenant = TenantId) => $"https://login.microsoftonline.com/{tenant}/v2.0";

    public EntraTokenOptions Options(Action<EntraTokenOptions>? configure = null)
    {
        var o = new EntraTokenOptions
        {
            ValidateBearerToken = true,
            TenantId = TenantId,
            ClientId = ServerClientId,
            AllowedClientIds = new[] { AllowedClientId },
            ResourceUri = ResourceUri,
        };
        configure?.Invoke(o);
        return o;
    }

    public EntraTokenPolicy Policy(Action<EntraTokenOptions>? configure = null) => EntraTokenPolicy.From(Options(configure), null);

    /// <summary>The key set document Entra would publish for this tenant (public parts only).</summary>
    public string Jwks(params (string Kid, RSA Key, string? Issuer)[] keys)
    {
        (string Kid, RSA Key, string? Issuer)[] list = keys.Length == 0 ? new (string, RSA, string?)[] { (KeyId, Key, null) } : keys;
        var array = new JsonArray();
        foreach (var (kid, key, issuer) in list)
        {
            var p = key.ExportParameters(false);
            var jwk = new JsonObject { ["kty"] = "RSA", ["use"] = "sig", ["kid"] = kid, ["n"] = B64(p.Modulus!), ["e"] = B64(p.Exponent!) };
            if (issuer is not null) jwk["issuer"] = issuer;
            array.Add(jwk);
        }
        return new JsonObject { ["keys"] = array }.ToJsonString();
    }

    public StubJwksHandler Handler() => new(() => Jwks());

    public JwksSigningKeyProvider Provider(EntraTokenPolicy policy, StubJwksHandler handler)
        => new(policy, new HttpClient(handler), Clock, NullLogger<JwksSigningKeyProvider>.Instance);

    public BearerTokenValidator Validator(EntraTokenPolicy policy, StubJwksHandler handler, ILogger<BearerTokenValidator>? logger = null)
        => new(policy, Provider(policy, handler), Clock, logger ?? NullLogger<BearerTokenValidator>.Instance);

    /// <summary>The claims of a good delegated token for the owner, as Entra v2 would issue them.</summary>
    public JsonObject Claims(Action<JsonObject>? edit = null)
    {
        var now = Clock.UtcNow;
        var claims = new JsonObject
        {
            ["iss"] = Issuer(),
            ["aud"] = ServerClientId,
            ["tid"] = TenantId,
            ["oid"] = ObjectId,
            ["sub"] = "sub-opaque-value",
            ["azp"] = AllowedClientId,
            ["ver"] = "2.0",
            ["scp"] = "user_impersonation",
            ["roles"] = new JsonArray("Adr.Admin"),
            ["preferred_username"] = UserName,
            ["name"] = "Owner Example",
            ["iat"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(60).ToUnixTimeSeconds(),
        };
        edit?.Invoke(claims);
        return claims;
    }

    /// <summary>Signs the claims. Pass <paramref name="alg"/> "none" for an unsigned token, "HS256" for the public-key-as-secret attack.</summary>
    public string Mint(JsonObject? claims = null, string alg = "RS256", string? kid = KeyId, RSA? signWith = null, Action<JsonObject>? editHeader = null)
    {
        var header = new JsonObject { ["typ"] = "JWT", ["alg"] = alg };
        if (kid is not null) header["kid"] = kid;
        editHeader?.Invoke(header);
        var signingInput = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes((claims ?? Claims()).ToJsonString()));
        var data = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature = alg switch
        {
            "none" => Array.Empty<byte>(),
            "HS256" => HMACSHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToBase64String(Key.ExportRSAPublicKey())), data),
            "RS384" => (signWith ?? Key).SignData(data, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
            _ => (signWith ?? Key).SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };
        return signingInput + "." + B64(signature);
    }

    public static string Bearer(string token) => "Bearer " + token;

    public static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Replaces one segment of a token (0 header, 1 payload, 2 signature) without re-signing it.</summary>
    public static string Tamper(string token, int segment, Func<string, string> edit)
    {
        var parts = token.Split('.');
        parts[segment] = edit(parts[segment]);
        return string.Join('.', parts);
    }

    public static string PayloadWith(string token, Action<JsonObject> edit)
    {
        var padded = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var payload = JsonNode.Parse(Convert.FromBase64String(padded))!.AsObject();
        edit(payload);
        return B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Collects every log line so a test can prove nothing sensitive was written.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (exception is not null) line += " | " + exception;
            lock (_owner.Lines) _owner.Lines.Add(line);
        }
    }
}
