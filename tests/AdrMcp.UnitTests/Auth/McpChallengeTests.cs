using AdrMcp.Application.Abstractions;
using AdrMcp.Infrastructure.Auth;

namespace AdrMcp.UnitTests.Auth;

/// <summary>The HTTP challenges the MCP authorization spec (revision 2026-07-28) asks for, character for character.</summary>
public class McpChallengeTests
{
    private const string Prm = "https://func-adr-test.azurewebsites.net/.well-known/oauth-protected-resource/runtime/webhooks/mcp";
    private const string Scope = "api://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/user_impersonation";

    [Fact]
    public void No_credentials_is_401_with_the_metadata_url_and_scope_and_no_error_code()
    {
        var c = McpChallenge.For(BearerFailure.MissingToken, Prm, Scope);
        Assert.Equal(401, c.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{Prm}\", scope=\"{Scope}\"", c.WwwAuthenticate);   // RFC 6750 3.1: no error when nothing was sent
        Assert.DoesNotContain("error=", c.WwwAuthenticate);
        Assert.Null(c.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(BearerFailure.Malformed)]
    [InlineData(BearerFailure.Expired)]
    [InlineData(BearerFailure.NotYetValid)]
    [InlineData(BearerFailure.WrongAudience)]
    [InlineData(BearerFailure.WrongIssuer)]
    [InlineData(BearerFailure.WrongTenant)]
    [InlineData(BearerFailure.WrongClient)]
    [InlineData(BearerFailure.UnsupportedAlgorithm)]
    [InlineData(BearerFailure.InvalidSignature)]
    [InlineData(BearerFailure.UnknownKey)]
    [InlineData(BearerFailure.TokenTooLarge)]
    public void A_bad_token_is_401_invalid_token_with_the_same_generic_text_whatever_the_reason(BearerFailure failure)
    {
        var c = McpChallenge.For(failure, Prm, Scope);
        Assert.Equal(401, c.StatusCode);
        Assert.Equal($"Bearer error=\"invalid_token\", error_description=\"The access token is not valid for this server.\", resource_metadata=\"{Prm}\", scope=\"{Scope}\"", c.WwwAuthenticate);
        Assert.Equal("invalid_token", c.Error);
    }

    [Theory]
    [InlineData(BearerFailure.InsufficientScope)]
    [InlineData(BearerFailure.ApplicationTokenNotAllowed)]
    public void Not_enough_scope_is_403_insufficient_scope_with_the_scope_and_metadata_url(BearerFailure failure)
    {
        var c = McpChallenge.For(failure, Prm, Scope);
        Assert.Equal(403, c.StatusCode);
        Assert.Equal($"Bearer error=\"insufficient_scope\", error_description=\"The access token does not carry the required scope.\", scope=\"{Scope}\", resource_metadata=\"{Prm}\"", c.WwwAuthenticate);
    }

    [Fact]
    public void A_caller_without_an_adr_role_is_a_plain_403_because_signing_in_again_cannot_fix_an_assignment()
    {
        var c = McpChallenge.NoRole();
        Assert.Equal(403, c.StatusCode);
        Assert.Null(c.WwwAuthenticate);   // an insufficient_scope challenge would send the client round a step-up loop for a scope it already holds
        Assert.Equal("forbidden", c.Error);
    }

    [Fact]
    public void Unavailable_keys_are_503_with_retry_after_and_no_challenge()
    {
        var c = McpChallenge.For(BearerFailure.KeysUnavailable, Prm, Scope);
        Assert.Equal(503, c.StatusCode);
        Assert.Null(c.WwwAuthenticate);   // the credentials may be fine: do not send the client off to sign in again
        Assert.Equal(McpChallenge.RetryAfterSecondsOnUnavailable, c.RetryAfterSeconds);
    }

    [Fact]
    public void Every_failure_maps_to_exactly_the_status_the_challenge_uses()
    {
        foreach (var failure in Enum.GetValues<BearerFailure>().Where(f => f != BearerFailure.None))
            Assert.Equal(BearerResult.Fail(failure).HttpStatus, McpChallenge.For(failure, Prm, Scope).StatusCode);
    }

    [Fact]
    public void Every_failure_has_a_distinct_content_free_reason_code()
    {
        var codes = Enum.GetValues<BearerFailure>().Select(f => BearerResult.Fail(f).ReasonCode).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z_]+$", c));
    }

    [Theory]
    [InlineData("https://x.example/\" injected=\"1")]
    [InlineData("https://x.example/\r\nSet-Cookie: a=b")]
    [InlineData("")]
    public void A_value_that_could_break_the_header_is_refused_not_escaped(string badUrl)
    {
        Assert.Throws<ArgumentException>(() => McpChallenge.For(BearerFailure.MissingToken, badUrl, Scope));
        Assert.Throws<ArgumentException>(() => McpChallenge.For(BearerFailure.MissingToken, Prm, badUrl));
    }

    [Theory]
    [InlineData("https://h.example/runtime/webhooks/mcp", "https://h.example/.well-known/oauth-protected-resource/runtime/webhooks/mcp")]
    [InlineData("https://h.example/runtime/webhooks/mcp/", "https://h.example/.well-known/oauth-protected-resource/runtime/webhooks/mcp")]
    [InlineData("https://h.example", "https://h.example/.well-known/oauth-protected-resource")]
    [InlineData("https://h.example/", "https://h.example/.well-known/oauth-protected-resource")]
    [InlineData("https://h.example:8443/mcp", "https://h.example:8443/.well-known/oauth-protected-resource/mcp")]
    public void The_metadata_url_inserts_the_well_known_segment_between_host_and_path_as_rfc_9728_says(string resource, string expected)
        => Assert.Equal(expected, ProtectedResourceMetadata.MetadataUrl(resource));
}
