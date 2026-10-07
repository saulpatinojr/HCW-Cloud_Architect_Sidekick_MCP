using System.Text.RegularExpressions;
using AdrMcp.Application.Abstractions;

namespace AdrMcp.Analysis.Safety;

/// <summary>
/// Deterministic secret patterns. Each is a regex with a type, severity and category. This list is intentionally conservative
/// and documented as incomplete: it does not do entropy analysis and cannot recognise opaque secrets without context.
/// </summary>
public static class SecretPatterns
{
    public sealed record Pattern(string Type, Regex Regex, SafetySeverity Severity, string Category, int Group = 0);

    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    public static readonly IReadOnlyList<Pattern> All = new List<Pattern>
    {
        new("AzureStorageAccountKey", new(@"AccountKey=([A-Za-z0-9+/=]{60,100})", O | RegexOptions.IgnoreCase), SafetySeverity.Critical, "credential", 1),
        new("AzureSasToken", new(@"(?:\?|&|^|\s)(?:sv=\d{4}-\d{2}-\d{2}&[^\s""']*sig=|sig=)([A-Za-z0-9%+/=]{20,})", O), SafetySeverity.Critical, "credential", 1),
        new("ConnectionStringPassword", new(@"(?i)(?:Password|Pwd)\s*=\s*([^;""'\s]{4,})", O), SafetySeverity.Critical, "credential", 1),
        new("ServiceBusSharedAccessKey", new(@"SharedAccessKey=([A-Za-z0-9+/=]{30,})", O), SafetySeverity.Critical, "credential", 1),
        new("PrivateKeyBlock", new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----", O), SafetySeverity.Critical, "credential"),
        new("AwsAccessKeyId", new(@"\b((?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA|ANVA)[0-9A-Z]{16})\b", O), SafetySeverity.Critical, "credential", 1),
        new("AwsSecretAccessKey", new(@"(?i)aws(?:.{0,20})?(?:secret|private)(?:.{0,20})?[:=]\s*[""']?([A-Za-z0-9/+=]{40})[""']?", O), SafetySeverity.Critical, "credential", 1),
        new("GitHubToken", new(@"\b((?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{82})\b", O), SafetySeverity.Critical, "credential", 1),
        new("SlackToken", new(@"\b(xox[baprs]-[A-Za-z0-9-]{10,})\b", O), SafetySeverity.High, "credential", 1),
        new("GoogleApiKey", new(@"\b(AIza[0-9A-Za-z_-]{35})\b", O), SafetySeverity.High, "credential", 1),
        new("StripeKey", new(@"\b((?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{20,})\b", O), SafetySeverity.Critical, "credential", 1),
        new("JsonWebToken", new(@"\b(eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b", O), SafetySeverity.High, "token", 1),
        new("BearerToken", new(@"(?i)\bBearer\s+([A-Za-z0-9._~+/=-]{20,})", O), SafetySeverity.High, "token", 1),
        new("AzureClientSecret", new(@"(?i)(?:client[_-]?secret|AZURE_CLIENT_SECRET)\s*[:=]\s*[""']?([A-Za-z0-9~._-]{20,})[""']?", O), SafetySeverity.Critical, "credential", 1),
        new("GenericApiKey", new(@"(?i)\b(?:api[_-]?key|apikey|x-api-key|access[_-]?token|auth[_-]?token|secret[_-]?key)\b\s*[:=]\s*[""']?([A-Za-z0-9_\-/+=.]{16,})[""']?", O), SafetySeverity.High, "credential", 1),
        new("PasswordAssignment", new(@"(?i)\b(?:password|passwd|pwd|secret)\b\s*[:=]\s*[""']?([^\s""';,]{6,})[""']?", O), SafetySeverity.High, "credential", 1),
        new("FunctionsKeyHeader", new(@"(?i)x-functions-key\s*[:=]\s*[""']?([A-Za-z0-9_=-]{20,})", O), SafetySeverity.Critical, "credential", 1),
        new("BasicAuthInUrl", new(@"(?i)\b[a-z][a-z0-9+.-]*://[^/\s:@]+:([^@\s/]+)@", O), SafetySeverity.Critical, "credential", 1),
        new("CosmosDbKey", new(@"(?i)(?:AccountKey|PrimaryKey|primaryMasterKey)\s*[:=]\s*[""']?([A-Za-z0-9+/=]{80,})", O), SafetySeverity.Critical, "credential", 1),
    };
}

/// <summary>Deterministic PII patterns with validation (Luhn, IBAN mod-97) to limit false positives.</summary>
public static class PiiPatterns
{
    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    public static readonly Regex Email = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", O);
    public static readonly Regex Phone = new(@"(?<![\w/.-])(?:\+?\d{1,3}[\s.-]?)?(?:\(?\d{3}\)?[\s.-]?)\d{3}[\s.-]?\d{4}\b", O);
    public static readonly Regex UsSsn = new(@"\b(?!000|666|9\d{2})\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b", O);
    public static readonly Regex CardCandidate = new(@"\b(?:\d[ -]?){13,19}\b", O);
    public static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", O);
    public static readonly Regex UkNino = new(@"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b", O);

    public static bool Luhn(string digits)
    {
        var d = digits.Where(char.IsDigit).ToArray();
        if (d.Length is < 13 or > 19) return false;
        int sum = 0; bool alt = false;
        for (int i = d.Length - 1; i >= 0; i--)
        {
            int n = d[i] - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n; alt = !alt;
        }
        return sum % 10 == 0;
    }

    public static bool IbanValid(string iban)
    {
        var s = iban.ToUpperInvariant();
        if (s.Length < 15) return false;
        var rearranged = s[4..] + s[..4];
        int rem = 0;
        foreach (var c in rearranged)
        {
            var v = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            foreach (var ch in v.ToString()) rem = (rem * 10 + (ch - '0')) % 97;
        }
        return rem == 1;
    }
}

/// <summary>Heuristic prompt-injection indicators. Deterministic; flags and neutralises rather than claiming certainty.</summary>
public static class PromptInjectionPatterns
{
    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    public static readonly IReadOnlyList<(string Name, Regex Regex)> All = new List<(string, Regex)>
    {
        ("IgnoreInstructions", new(@"\b(ignore|disregard|forget)\b.{0,40}\b(previous|prior|above|earlier|all)\b.{0,20}\b(instructions?|prompts?|rules?)\b", O)),
        ("RoleOverride", new(@"\byou are now\b|\bact as (?:an?|the) (?:system|admin|developer|root)\b|\bnew (?:system )?instructions?:", O)),
        ("SystemPromptProbe", new(@"\b(reveal|print|show|dump)\b.{0,30}\b(system prompt|hidden prompt|your instructions)\b", O)),
        ("ToolAbuse", new(@"\b(call|invoke|run|execute)\b.{0,30}\b(adr_approve_change|adr_update|adr_archive|adr_supersede|evidence_remove|delete|rm -rf)\b", O)),
        ("ChatMarkup", new(@"<\|im_start\|>|<\|im_end\|>|\[INST\]|\[/INST\]|^\s*(?:assistant|system)\s*:", O | RegexOptions.Multiline)),
        ("HiddenHtmlInstruction", new(@"<!--[\s\S]{0,400}?\b(instruction|ignore|assistant|system|prompt)\b[\s\S]{0,400}?-->", O)),
        ("ZeroWidthCharacters", new(@"[​‌‍⁠﻿]{3,}", O)),
    };
}
