using System.Net.Http;
using System.Security.Cryptography;
using AdrMcp.Infrastructure.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdrMcp.UnitTests.Auth;

/// <summary>The signing-key cache: when it fetches, when it refuses to, and that it never invents an answer (ADR-0008).</summary>
public class JwksSigningKeyProviderTests : IDisposable
{
    private readonly TestIdp _idp = new();
    private readonly List<IDisposable> _extra = new();

    public void Dispose()
    {
        _idp.Dispose();
        foreach (var d in _extra) d.Dispose();
    }

    private (JwksSigningKeyProvider Provider, StubJwksHandler Handler) Make(Action<AdrMcp.Application.Options.EntraTokenOptions>? configure = null)
    {
        var handler = _idp.Handler();
        return (_idp.Provider(_idp.Policy(configure), handler), handler);
    }

    [Fact]
    public async Task The_first_lookup_fetches_and_later_lookups_use_the_cache()
    {
        var (provider, handler) = Make();
        for (var i = 0; i < 5; i++) Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Keys_are_fetched_again_only_after_the_cache_lifetime()
    {
        var (provider, handler) = Make(o => o.JwksCacheMinutes = 30);
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        _idp.Clock.Advance(TimeSpan.FromMinutes(29));
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        Assert.Equal(1, handler.Requests);
        _idp.Clock.Advance(TimeSpan.FromMinutes(2));
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task The_fetch_goes_to_the_tenant_key_address_built_from_the_authority_host_and_tenant()
    {
        var (provider, handler) = Make();
        Uri? seen = null;
        handler.Override = r => { seen = r.RequestUri; return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_idp.Jwks()) }; };
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        Assert.Equal($"https://login.microsoftonline.com/{TestIdp.TenantId}/discovery/v2.0/keys", seen!.ToString());
    }

    [Fact]
    public async Task A_new_key_after_rotation_is_picked_up_by_one_refresh()
    {
        using var next = RSA.Create(2048);
        var published = _idp.Jwks();
        var handler = new StubJwksHandler(() => published);
        var provider = _idp.Provider(_idp.Policy(), handler);
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);

        published = _idp.Jwks((TestIdp.KeyId, _idp.Key, null), ("kid-next", next, null));
        _idp.Clock.Advance(JwksSigningKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync("kid-next", default)).Status);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Random_key_ids_cannot_make_the_app_fetch_more_than_once_a_minute()
    {
        var (provider, handler) = Make();
        await provider.GetKeyAsync(TestIdp.KeyId, default);                       // fetch 1
        for (var i = 0; i < 50; i++) Assert.Equal(KeyLookupStatus.NotFound, (await provider.GetKeyAsync("random-" + i, default)).Status);
        Assert.Equal(1, handler.Requests);
        _idp.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(KeyLookupStatus.NotFound, (await provider.GetKeyAsync("random-after", default)).Status);   // one more refresh is allowed
        Assert.Equal(2, handler.Requests);
        for (var i = 0; i < 50; i++) await provider.GetKeyAsync("again-" + i, default);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Known_keys_keep_working_while_an_unknown_key_id_refresh_fails()
    {
        var (provider, handler) = Make();
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        _idp.Clock.Advance(JwksSigningKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));
        handler.Fail = true;
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync("kid-unknown", default)).Status);   // cannot tell: 503, not 401
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);        // the cached key is still inside its lifetime
    }

    [Fact]
    public async Task Expired_keys_are_never_used_when_the_refresh_fails()
    {
        var (provider, handler) = Make(o => o.JwksCacheMinutes = 10);
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
        _idp.Clock.Advance(TimeSpan.FromMinutes(11));
        handler.Fail = true;
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
        _idp.Clock.Advance(JwksSigningKeyProvider.FailureBackoff + TimeSpan.FromSeconds(1));
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
    }

    [Fact]
    public async Task A_recovered_key_endpoint_is_used_again_after_the_failure_backoff()
    {
        var (provider, handler) = Make();
        handler.Fail = true;
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
        handler.Fail = false;
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);   // inside the backoff: not retried yet
        Assert.Equal(1, handler.Requests);
        _idp.Clock.Advance(JwksSigningKeyProvider.FailureBackoff + TimeSpan.FromSeconds(1));
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task A_burst_of_concurrent_first_requests_causes_one_fetch()
    {
        var (provider, handler) = Make();
        handler.Delay = TimeSpan.FromMilliseconds(150);
        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => provider.GetKeyAsync(TestIdp.KeyId, default)));
        Assert.All(results, r => Assert.Equal(KeyLookupStatus.Found, r.Status));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_slow_key_endpoint_times_out_as_unavailable()
    {
        var handler = _idp.Handler();
        handler.Delay = TimeSpan.FromSeconds(5);
        var provider = new JwksSigningKeyProvider(_idp.Policy(), new HttpClient(handler), _idp.Clock, NullLogger<JwksSigningKeyProvider>.Instance, fetchTimeout: TimeSpan.FromMilliseconds(150));
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_a_key_outage()
    {
        var (provider, handler) = Make();
        handler.Delay = TimeSpan.FromSeconds(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetKeyAsync(TestIdp.KeyId, cts.Token));
    }

    [Fact]
    public async Task Only_rsa_signing_keys_with_an_id_are_kept()
    {
        var p = _idp.Key.ExportParameters(false);
        var n = TestIdp.B64(p.Modulus!); var e = TestIdp.B64(p.Exponent!);
        var doc = $$"""
            {"keys":[
              {"kty":"RSA","use":"enc","kid":"enc-key","n":"{{n}}","e":"{{e}}"},
              {"kty":"RSA","use":"sig","n":"{{n}}","e":"{{e}}"},
              {"kty":"EC","use":"sig","kid":"ec-key","crv":"P-256","x":"AAAA","y":"AAAA"},
              {"kty":"RSA","kid":"no-use-is-ok","n":"{{n}}","e":"{{e}}"}
            ]}
            """;
        var handler = new StubJwksHandler(() => doc);
        var provider = _idp.Provider(_idp.Policy(), handler);
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync("no-use-is-ok", default)).Status);
        Assert.Equal(KeyLookupStatus.NotFound, (await provider.GetKeyAsync("enc-key", default)).Status);
        Assert.Equal(KeyLookupStatus.NotFound, (await provider.GetKeyAsync("ec-key", default)).Status);
    }

    [Fact]
    public async Task An_empty_key_id_is_not_found_without_a_fetch()
    {
        var (provider, handler) = Make();
        Assert.Equal(KeyLookupStatus.NotFound, (await provider.GetKeyAsync("", default)).Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Failures_are_logged_with_a_fixed_reason_and_nothing_from_the_response()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var handler = _idp.Handler();
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"secret-looking\":\"abc.def.ghi-this-must-not-be-logged\"") };
        var provider = new JwksSigningKeyProvider(_idp.Policy(), new HttpClient(handler), _idp.Clock, factory.CreateLogger<JwksSigningKeyProvider>());
        await provider.GetKeyAsync(TestIdp.KeyId, default);

        var line = Assert.Single(logs.Lines);
        Assert.Equal("Signing key refresh failed (invalid_document)", line);
    }

    private sealed class SizedContent : HttpContent
    {
        private readonly byte[] _body;
        private readonly long? _declared;
        public SizedContent(string body, long? declared) { _body = System.Text.Encoding.UTF8.GetBytes(body); _declared = declared; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(_body).AsTask();
        protected override bool TryComputeLength(out long length) { length = _declared ?? 0; return _declared.HasValue; }
    }

    [Fact]
    public async Task A_declared_length_over_the_cap_is_refused_even_when_the_body_is_short()
    {
        var (provider, handler) = Make();
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new SizedContent(_idp.Jwks(), JwksSigningKeyProvider.MaxDocumentBytes + 1) };
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
    }

    [Fact]
    public async Task A_body_over_the_cap_with_no_declared_length_is_refused_not_truncated_and_accepted()
    {
        // The valid key set sits inside the first bytes, so a reader that merely stopped at the cap would still find a key.
        var (provider, handler) = Make();
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new SizedContent(_idp.Jwks() + new string(' ', JwksSigningKeyProvider.MaxDocumentBytes + 10), null) };
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync(TestIdp.KeyId, default)).Status);
    }

    [Fact]
    public async Task A_failed_refresh_for_an_unknown_key_id_keeps_answering_unavailable_inside_the_window_without_another_fetch()
    {
        var (provider, handler) = Make();
        await provider.GetKeyAsync(TestIdp.KeyId, default);
        _idp.Clock.Advance(JwksSigningKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));
        handler.Fail = true;
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync("unknown-1", default)).Status);
        Assert.Equal(KeyLookupStatus.Unavailable, (await provider.GetKeyAsync("unknown-2", default)).Status);   // still "could not tell", not "unknown"
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task A_caller_that_gives_up_mid_fetch_does_not_use_up_the_refresh_budget_for_the_next_caller()
    {
        using var next = RSA.Create(2048);
        var published = _idp.Jwks();
        var handler = new StubJwksHandler(() => published);
        var provider = _idp.Provider(_idp.Policy(), handler);
        await provider.GetKeyAsync(TestIdp.KeyId, default);

        // Keys rotate; the first caller to notice disconnects while the refresh is under way.
        published = _idp.Jwks((TestIdp.KeyId, _idp.Key, null), ("kid-next", next, null));
        _idp.Clock.Advance(JwksSigningKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));
        handler.Delay = TimeSpan.FromSeconds(2);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetKeyAsync("kid-next", cts.Token));

        // The next caller, inside the same minute, must still be allowed to fetch and must find the rotated key.
        handler.Delay = TimeSpan.Zero;
        Assert.Equal(KeyLookupStatus.Found, (await provider.GetKeyAsync("kid-next", default)).Status);
    }
}
