using System.Text.RegularExpressions;

namespace AdrMcp.Application.Common;

/// <summary>
/// Last line of defence for log and error messages. Deterministic, fast, and deliberately over-eager.
/// This is NOT the content safety pipeline (see IContentSafetyService); it only protects telemetry.
/// </summary>
public static partial class SensitiveDataRedactor
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        (ConnectionStringKey(), "AccountKey=[REDACTED]"),
        (SasSignature(), "sig=[REDACTED]"),
        (SharedAccessSignature(), "SharedAccessSignature=[REDACTED]"),
        (BearerToken(), "Bearer [REDACTED]"),
        (FunctionsKeyHeader(), "x-functions-key: [REDACTED]"),
        (CodeQuery(), "code=[REDACTED]"),
        (PasswordKv(), "$1=[REDACTED]"),
        (PrivateKeyBlock(), "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----"),
        (Jwt(), "[REDACTED_JWT]"),
        (AwsAccessKey(), "[REDACTED_AWS_KEY]"),
        (GitHubToken(), "[REDACTED_GH_TOKEN]"),
        (Email(), "[REDACTED_EMAIL]"),
    };

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = input;
        foreach (var (pattern, replacement) in Rules) s = pattern.Replace(s, replacement);
        return s;
    }

    [GeneratedRegex(@"AccountKey=[^;""'\s]+", RegexOptions.IgnoreCase)] private static partial Regex ConnectionStringKey();
    [GeneratedRegex(@"sig=[A-Za-z0-9%+/=_-]{8,}", RegexOptions.IgnoreCase)] private static partial Regex SasSignature();
    [GeneratedRegex(@"SharedAccessSignature=[^;""'\s]+", RegexOptions.IgnoreCase)] private static partial Regex SharedAccessSignature();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{10,}", RegexOptions.IgnoreCase)] private static partial Regex BearerToken();
    [GeneratedRegex(@"x-functions-key\s*[:=]\s*[^\s,;]+", RegexOptions.IgnoreCase)] private static partial Regex FunctionsKeyHeader();
    [GeneratedRegex(@"([?&])?code=[A-Za-z0-9%_=-]{16,}", RegexOptions.IgnoreCase)] private static partial Regex CodeQuery();
    [GeneratedRegex(@"\b(password|pwd|passwd|secret|api[_-]?key|client[_-]?secret|token)\s*[:=]\s*[^\s;,""']+", RegexOptions.IgnoreCase)] private static partial Regex PasswordKv();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKeyBlock();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")] private static partial Regex Jwt();
    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")] private static partial Regex AwsAccessKey();
    [GeneratedRegex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
}
