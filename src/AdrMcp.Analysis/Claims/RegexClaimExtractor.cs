using System.Text.RegularExpressions;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Analysis.Claims;

/// <summary>
/// Deterministic extraction of candidate claims from decision text. Everything produced here is Declared=false and therefore
/// treated as inferred (capped confidence) by drift and contradiction analysis. Humans can promote a claim by declaring it.
/// </summary>
public sealed partial class RegexClaimExtractor : IClaimExtractor
{
    public IReadOnlyList<DecisionClaim> Extract(Adr adr)
    {
        var text = adr.Human.Decision + "\n" + adr.Human.Context + "\n" + adr.Human.Consequences;
        var scope = ExtractScope(text);
        var claims = new List<DecisionClaim>();
        foreach (var sentence in Sentences(text))
        {
            foreach (Match m in Prohibited().Matches(sentence)) Add(claims, ClaimKind.ProhibitedTechnology, Clean(m.Groups["tech"].Value), "must not use", null, scope, sentence);
            foreach (Match m in Required().Matches(sentence)) Add(claims, ClaimKind.RequiredTechnology, Clean(m.Groups["tech"].Value), "must use", null, scope, sentence);
            foreach (Match m in DbOwnership().Matches(sentence)) Add(claims, ClaimKind.DatabaseOwnership, Clean(m.Groups["svc"].Value), "owns", Clean(m.Groups["db"].Value), scope, sentence);
            foreach (Match m in Integration().Matches(sentence)) Add(claims, ClaimKind.IntegrationStyle, "integration", "is", m.Groups["style"].Value.ToLowerInvariant().StartsWith("sync") ? "sync" : "async", scope, sentence);
            foreach (Match m in Cloud().Matches(sentence)) Add(claims, ClaimKind.CloudProvider, "deployment", "targets", NormalizeCloud(m.Groups["cloud"].Value), scope, sentence);
            foreach (Match m in Auth().Matches(sentence)) Add(claims, ClaimKind.AuthStrategy, "authentication", "uses", NormalizeAuth(m.Groups["auth"].Value), scope, sentence);
            foreach (Match m in Residency().Matches(sentence)) Add(claims, ClaimKind.DataResidency, "data", "resides in", Clean(m.Groups["region"].Value), scope, sentence);
            foreach (Match m in Infra().Matches(sentence)) Add(claims, ClaimKind.InfrastructureRequirement, Clean(m.Groups["res"].Value), "must have", Clean(m.Groups["req"].Value), scope, sentence);
        }
        return claims;
    }

    private static void Add(List<DecisionClaim> list, ClaimKind kind, string subject, string? predicate, string? obj, ClaimScope scope, string sentence)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 120) return;
        var id = "x-" + Hashing.Sha256Hex($"{kind}|{DecisionClaim.Normalize(subject)}|{(obj is null ? "" : DecisionClaim.Normalize(obj))}")[..10];
        if (list.Any(c => c.ClaimId == id)) return;
        list.Add(new DecisionClaim(id, kind, subject, predicate, obj, scope, sentence.Trim(), Declared: false));
    }

    public static ClaimScope ExtractScope(string text)
    {
        var env = EnvRx().Match(text);
        var bc = BoundedContext().Match(text);
        var sys = SystemRx().Match(text);
        return new ClaimScope(
            System: sys.Success ? Clean(sys.Groups["sys"].Value) : null,
            BoundedContext: bc.Success ? Clean(bc.Groups["bc"].Value) : null,
            Environment: env.Success ? env.Groups["env"].Value.ToLowerInvariant() switch { "prod" or "production" => "production", "dev" or "development" => "development", "staging" or "stage" => "staging", "test" or "qa" => "test", var e => e } : null);
    }

    private static IEnumerable<string> Sentences(string text) => Regex.Split(text.Replace("\r", ""), @"(?<=[.!?])\s+|\n+").Where(s => s.Trim().Length > 8);

    private static string Clean(string s) => s.Trim().Trim('`', '"', '\'', '*', '.', ',', ';', ':', ')', '(').Trim();

    private static string NormalizeCloud(string c) => c.ToLowerInvariant() switch { "azure" or "microsoft azure" => "azure", "aws" or "amazon web services" => "aws", "gcp" or "google cloud" or "google cloud platform" => "gcp", var o => o };
    private static string NormalizeAuth(string a) { var l = a.ToLowerInvariant(); return l.Contains("entra") || l.Contains("azure ad") || l.Contains("aad") ? "entra-id" : l.Contains("oauth") || l.Contains("oidc") || l.Contains("openid") ? "oauth2-oidc" : l.Contains("saml") ? "saml" : l.Contains("api key") || l.Contains("api-key") ? "api-key" : l.Contains("basic") ? "basic" : l.Contains("certificate") || l.Contains("mtls") ? "mtls" : l.Contains("managed identit") ? "managed-identity" : l; }

    [GeneratedRegex(@"\b(?:must not|shall not|may not|will not|should not|do not|don't|never|is prohibited to|are prohibited from|prohibit(?:s|ed)?|ban(?:s|ned)?|forbid(?:s|den)?|avoid|is not (?:allowed|permitted)|are not (?:allowed|permitted)|no longer)\s+(?:use|using|adopt|depend on|rely on|introduce|be used|of|the use of|to use|allowed:?|permitted:?)?\s*(?:the\s+)?(?<tech>[A-Z@][A-Za-z0-9@./_+#-]{1,60}(?:\s+[A-Z][A-Za-z0-9.+#-]{1,30}){0,3})", RegexOptions.CultureInvariant)] private static partial Regex Prohibited();
    [GeneratedRegex(@"\b(?:must use|shall use|will use|we use|use only|standardi[sz]e on|adopt|is mandatory|are mandatory|is required|are required|must be used|must adopt|exclusively use|is the standard|will adopt|we will adopt|we adopt)\s+(?:for\s+\w+\s+)?(?:the\s+|only\s+)?(?<tech>[A-Z@][A-Za-z0-9@./_+#-]{1,60}(?:\s+[A-Z][A-Za-z0-9.+#-]{1,30}){0,3})", RegexOptions.CultureInvariant)] private static partial Regex Required();
    [GeneratedRegex(@"\b(?<svc>[A-Z][A-Za-z0-9]+(?:\s?Service|Svc|API|Api)?)\s+(?:exclusively\s+)?(?:owns|is the (?:sole|only|exclusive) owner of|is the system of record for|has exclusive ownership of)\s+(?:the\s+)?(?:database\s+|db\s+)?(?<db>[A-Z][A-Za-z0-9_-]+(?:\s?(?:DB|Db|Database))?)", RegexOptions.CultureInvariant)] private static partial Regex DbOwnership();
    [GeneratedRegex(@"\b(?:communicat\w*|integrat\w*|interact\w*|call\w*|invoke\w*|exchange messages?)\s+(?:\w+\s+){0,4}?(?<style>synchronous(?:ly)?|asynchronous(?:ly)?|sync|async)\b|\b(?<style2>synchronous|asynchronous|event-driven|message-based|queue-based|request/response|request-response)\s+(?:integration|communication|messaging|calls?|interaction)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex IntegrationRaw();
    private static Regex Integration() => IntegrationComposite ??= new Regex(IntegrationRaw().ToString().Replace("(?<style2>", "(?<style>"), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static Regex? IntegrationComposite;
    [GeneratedRegex(@"\b(?:deploy(?:ed)?|host(?:ed)?|run(?:s)?|provision(?:ed)?|migrat(?:e|ed)|standardi[sz]e)\s+(?:\w+\s+){0,3}?(?:to|on|in|onto)\s+(?<cloud>Microsoft Azure|Azure|AWS|Amazon Web Services|GCP|Google Cloud Platform|Google Cloud)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex Cloud();
    [GeneratedRegex(@"\b(?:authenticat\w*|authoriz\w*|auth)\s+(?:\w+\s+){0,3}?(?:via|with|using|through|by)\s+(?<auth>Microsoft Entra ID|Entra ID|Azure AD|AAD|OAuth 2\.?[01]?|OAuth|OIDC|OpenID Connect|SAML|API keys?|basic auth(?:entication)?|client certificates?|mTLS|managed identit(?:y|ies))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex Auth();
    [GeneratedRegex(@"\bdata\s+(?:must|shall|will)?\s*(?:be\s+)?(?:reside|resides|remain|remains|be stored|stored|be kept|kept|be hosted|hosted|be processed|processed)\s+(?:only\s+|exclusively\s+)?(?:in|within)\s+(?:the\s+)?(?<region>EU|European Union|Europe|US|United States|UK|Canada|Australia|Germany|France|India|Japan|Switzerland|[A-Z][a-z]+ [A-Z][a-z]+|[A-Z][a-z]+)\b", RegexOptions.CultureInvariant)] private static partial Regex Residency();
    [GeneratedRegex(@"\b(?:all|every|each)\s+(?<res>storage accounts?|databases?|key vaults?|function apps?|app services?|web apps?|virtual machines?|VMs|containers?|container apps?|AKS clusters?|clusters?|Cosmos DB accounts?|SQL servers?|service bus namespaces?|resources?|public endpoints?)\s+(?:must|shall|will|are required to|need to|should)\s+(?:be\s+|have\s+|use\s+|enable\s+|enforce\s+|require\s+|run with\s+|be configured with\s+)?(?<req>(?:private endpoints?|TLS 1\.[23]|HTTPS only|https only|managed identit(?:y|ies)|zone[- ]redundan\w+|geo[- ]redundan\w+|soft[- ]delete|purge protection|customer[- ]managed keys?|CMK|encryption at rest|diagnostic settings?|no public (?:network )?access|public network access disabled|shared key access disabled|disabled shared key access|infrastructure encryption|RBAC(?: authorization)?|Microsoft Defender|a (?:private|regional) endpoint|at least \d+ replicas?|backups?|point[- ]in[- ]time restore|minimum TLS (?:version )?1\.[23]|network rules|firewall rules|a firewall|tags?))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex Infra();
    [GeneratedRegex(@"\b(?:in|for|on)\s+(?:the\s+)?(?<env>production|prod|staging|stage|development|dev|test|qa)\b(?:\s+environments?)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex EnvRx();
    [GeneratedRegex(@"\b(?:in|for|within)\s+the\s+(?<bc>[A-Z][A-Za-z]+(?:\s[A-Z][A-Za-z]+)?)\s+(?:bounded context|domain|subdomain)\b", RegexOptions.CultureInvariant)] private static partial Regex BoundedContext();
    [GeneratedRegex(@"\b(?:for|in|within)\s+the\s+(?<sys>[A-Z][A-Za-z0-9]+(?:\s[A-Z][A-Za-z0-9]+)?)\s+(?:system|platform|application|service)\b", RegexOptions.CultureInvariant)] private static partial Regex SystemRx();
}
