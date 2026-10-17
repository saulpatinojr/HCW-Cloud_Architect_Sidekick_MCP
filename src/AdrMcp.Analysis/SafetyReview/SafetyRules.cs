using System.Globalization;
using System.Text.RegularExpressions;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Findings;
using Microsoft.Extensions.Options;

namespace AdrMcp.Analysis.SafetyReview;

public sealed class SafetyPolicyOptions
{
    public const string Section = "SafetyPolicy";
    /// <summary>Rule ids whose findings hard-block acceptance. Empty by default: the system is advisory.</summary>
    public string[] BlockingRuleIds { get; set; } = Array.Empty<string>();
    public string PolicyId { get; set; } = "org-default";
}

public sealed class ConfigurationPolicyProvider : IPolicyProvider
{
    private readonly SafetyPolicyOptions _o;
    public ConfigurationPolicyProvider(IOptions<SafetyPolicyOptions> o) => _o = o.Value;
    public bool BlocksAcceptance(string ruleId) => _o.BlockingRuleIds.Contains(ruleId, StringComparer.OrdinalIgnoreCase);
    public string? PolicyIdFor(string ruleId) => BlocksAcceptance(ruleId) ? _o.PolicyId : null;
}

internal static class RuleText
{
    public static string All(Adr a) => string.Join("\n", a.Human.Title, a.Human.Context, a.Human.Decision, a.Human.Rationale, string.Join("\n", a.Human.Alternatives), a.Human.Consequences, a.Human.HumanNotes ?? "");
    public static string DecisionAndConsequences(Adr a) => a.Human.Decision + "\n" + a.Human.Consequences;
    public static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string? First(string text, string pattern) { var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); return m.Success ? Trim(m.Value) : null; }
    public static IEnumerable<string> AllMatches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(m => Trim(m.Value)).Distinct();
    private static string Trim(string s) => s.Length <= 160 ? s.Trim() : s[..160].Trim() + "…";
}

/// <summary>Base class that fills the common fields; concrete rules describe what they observed, inferred and recommend.</summary>
public abstract class SafetyRuleBase : ISafetyReviewRule
{
    public abstract string RuleId { get; }
    public abstract string Category { get; }
    public abstract IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create);

    protected static SafetyFinding Build(Func<string, SafetyFinding> create, string summary, FindingSeverity severity, double confidence, string observed, string inference, string recommendation, string[] qualityAttributes, string[] mitigations, string[]? alternatives = null)
        => create(summary) with
        {
            Severity = severity,
            Confidence = Confidence.Inferred(confidence, "Heuristic text rule; verify against the actual design."),
            ObservedEvidence = observed,
            Inference = inference,
            Recommendation = recommendation,
            AffectedQualityAttributes = qualityAttributes,
            SuggestedMitigations = mitigations,
            AlternativesToConsider = alternatives ?? Array.Empty<string>(),
            Explanation = $"Observed: {observed} | Inferred: {inference} | Recommendation: {recommendation}",
            RequiresHumanReview = severity >= FindingSeverity.Medium
        };
}

public sealed partial class EolTechnologyRule : SafetyRuleBase
{
    /// <summary>Days before a .NET release's end of support during which a Medium "approaching end of support" finding is emitted.</summary>
    public const int DotNetEolWarningWindowDays = 90;

    private sealed record DotNetRelease(int Major, int? Minor, string Name, bool Lts, DateOnly EndOfSupport);

    // Official end-of-support dates (the last supported day), verified 2026-10-17 against the .NET support policy
    // (https://dotnet.microsoft.com/platform/support/policy/dotnet-core, last updated 2026-10-13) and the Microsoft Lifecycle page
    // (https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core). .NET 9 is STS, now supported for 24 months, so it
    // ends on the same day as .NET 8. Minor is null for .NET 5+, which only ships x.0. .NET Core 1.x/2.0 are omitted: the previous
    // pattern never matched them and ".NET 1.1"/".NET 2.0" usually mean .NET Framework. Add the next release once Microsoft publishes it.
    private static readonly DotNetRelease[] DotNetReleases =
    {
        new(2, 1, ".NET Core 2.1", true, new DateOnly(2021, 8, 21)),
        new(2, 2, ".NET Core 2.2", false, new DateOnly(2019, 12, 23)),
        new(3, 0, ".NET Core 3.0", false, new DateOnly(2020, 3, 3)),
        new(3, 1, ".NET Core 3.1", true, new DateOnly(2022, 12, 13)),
        new(5, null, ".NET 5", false, new DateOnly(2022, 5, 10)),
        new(6, null, ".NET 6", true, new DateOnly(2024, 11, 12)),
        new(7, null, ".NET 7", false, new DateOnly(2024, 5, 14)),
        new(8, null, ".NET 8", true, new DateOnly(2026, 11, 10)),
        new(9, null, ".NET 9", false, new DateOnly(2026, 11, 10)),
        new(10, null, ".NET 10", true, new DateOnly(2028, 11, 14)),
    };

    // ".NET 8", ".NET 8.0", ".NET Core 3.1", "ASP.NET Core 3.1", "dotnet 6", "dotnet core 3.1", and TFMs "net8.0", "net8.0-windows", "netcoreapp3.1".
    // TFMs need the dot so .NET Framework monikers (net48, net472) and plain "net" (net 5 million) do not match.
    [GeneratedRegex(@"(?:\.NET\s*(?:Core\s*)?|\bdotnet\s*(?:core\s*)?|\bnet(?:coreapp)?(?=\d{1,2}\.\d))(?<major>\d{1,2})(?:\.(?<minor>\d+))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetVersion();

    // Dates verified against vendor lifecycle pages as of 2026-10-07. [VERIFY] before relying on exact dates in policy.
    // .NET is evaluated separately against DotNetReleases so its end of support is compared with the review date.
    private static readonly (string Pattern, string Name, string Note)[] Eol =
    {
        (@"\bNode(?:\.js)?\s*(?:v?1[0-8])\b", "Node.js 18 and earlier (18 EOL 2025-04-30)", "Use Node.js 20 LTS or later."),
        (@"\bPython\s*(?:2(?:\.\d+)?|3\.[0-8])\b", "Python 3.8 and earlier (3.8 EOL 2024-10-07)", "Use Python 3.10+."),
        (@"\bAngular\.?JS\b|\bAngular\s*1\b", "AngularJS (EOL 2022-01)", "Migrate to a supported framework."),
        (@"\bCentOS\s*(?:7|8)\b", "CentOS 7/8 (EOL 2024-06-30 / 2021-12-31)", "Use a supported distribution."),
        (@"\bUbuntu\s*(?:16|18)\.04\b", "Ubuntu 16.04/18.04 (standard support ended)", "Use Ubuntu 22.04/24.04 LTS."),
        (@"\bWindows Server\s*(?:2008|2012)(?:\s*R2)?\b", "Windows Server 2012 R2 and earlier (EOL 2023-10-10)", "Use Windows Server 2022+."),
        (@"\bSQL Server\s*(?:2008|2012|2014)(?:\s*R2)?\b", "SQL Server 2014 and earlier (2014 EOL 2024-07-09)", "Use SQL Server 2019+ or Azure SQL."),
        (@"\bTLS\s*1\.[01]\b", "TLS 1.0/1.1 (deprecated by RFC 8996)", "Require TLS 1.2+."),
        (@"\bAzure\s+(?:Service Manager|Classic)\b|\bASM\s+(?:VMs|deployment)\b", "Azure classic (ASM) deployment model (retired)", "Use Azure Resource Manager."),
        (@"\bAzure Functions\s*(?:v1|v2|v3|runtime [123])\b", "Azure Functions runtime v1-v3 (v3 EOL 2022-12-13)", "Use Functions v4 isolated worker."),
        (@"\bJava\s*(?:7|8)\b(?!\s*\+)", "Java 8 (public updates ended for most vendors)", "Use Java 17/21 LTS."),
    };
    public override string RuleId => "SR-EOL-001"; public override string Category => "UnsupportedTechnology";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        foreach (var finding in EvaluateDotNet(text, DateOnly.FromDateTime(input.Now.UtcDateTime), create)) yield return finding;
        foreach (var (pattern, name, note) in Eol)
        {
            var m = RuleText.First(text, pattern);
            if (m is null) continue;
            yield return Build(create, $"Decision references end-of-life or deprecated technology: {name}", FindingSeverity.High, 0.6, $"Text mentions '{m}'.", "The decision depends on a technology that no longer receives security updates.", note,
                new[] { "Security", "Supportability", "Maintainability" }, new[] { "Pin a supported version and document the upgrade path.", "Add a fitness function that fails on EOL runtimes." });
        }
    }

    /// <summary>
    /// A .NET release is end of life from the UTC day after its last supported day (High). From <see cref="DotNetEolWarningWindowDays"/>
    /// days before that last day, up to and including it, it is approaching end of support (Medium). Unknown versions are ignored.
    /// </summary>
    private static IEnumerable<SafetyFinding> EvaluateDotNet(string text, DateOnly today, Func<string, SafetyFinding> create)
    {
        var mentions = DotNetVersion().Matches(text)
            .Select(m => (Release: Resolve(m), Excerpt: m.Value.Trim()))
            .Where(x => x.Release is not null)
            .Select(x => (Release: x.Release!, x.Excerpt))
            .ToList();
        if (mentions.Count == 0) yield break;

        var target = DotNetReleases.Where(r => r.Lts && r.EndOfSupport.AddDays(-DotNetEolWarningWindowDays) > today).MaxBy(r => r.Major);
        string[] attributes = { "Security", "Supportability", "Maintainability" };

        var eol = mentions.Where(x => today > x.Release.EndOfSupport).ToList();
        if (eol.Count > 0)
            yield return Build(create, $"Decision references end-of-life or deprecated technology: {Describe(eol)}", FindingSeverity.High, 0.6, Observed(eol),
                "The decision depends on a technology that no longer receives security updates.",
                target is null ? "Upgrade to a supported .NET LTS release (see the official .NET support policy)." : $"Upgrade to a supported LTS ({target.Name} until {Format(target.EndOfSupport)}).",
                attributes, new[] { "Pin a supported version and document the upgrade path.", "Add a fitness function that fails on EOL runtimes." });

        var nearing = mentions.Where(x => today <= x.Release.EndOfSupport && today >= x.Release.EndOfSupport.AddDays(-DotNetEolWarningWindowDays)).ToList();
        if (nearing.Count > 0)
        {
            var deadline = Format(nearing.Min(x => x.Release.EndOfSupport));
            var remaining = string.Join("; ", Releases(nearing).Select(r => $"{r.Name} is supported through {Format(r.EndOfSupport)} ({DaysLeft(r.EndOfSupport.DayNumber - today.DayNumber)})"));
            yield return Build(create, $"Decision references technology approaching end of support: {Describe(nearing)}", FindingSeverity.Medium, 0.6, Observed(nearing),
                $"{remaining}. After that the decision depends on a technology that no longer receives security updates.",
                target is null ? $"Plan the upgrade to a supported .NET LTS release before {deadline}." : $"Plan the upgrade to {target.Name} (LTS until {Format(target.EndOfSupport)}) before {deadline}.",
                attributes, new[] { "Schedule the runtime upgrade before the end-of-support date.", "Add a fitness function that fails on EOL runtimes." });
        }
    }

    private static DotNetRelease? Resolve(Match m)
    {
        var major = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
        int? minor = m.Groups["minor"].Success && int.TryParse(m.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        return DotNetReleases.FirstOrDefault(r => r.Major == major && (r.Minor is null || r.Minor == minor));
    }

    private static IEnumerable<DotNetRelease> Releases(List<(DotNetRelease Release, string Excerpt)> mentions)
        => mentions.Select(x => x.Release).Distinct().OrderBy(r => r.Major).ThenBy(r => r.Minor ?? 0);
    private static string Describe(List<(DotNetRelease Release, string Excerpt)> mentions)
        => string.Join(", ", Releases(mentions).Select(r => $"{r.Name} (end of support {Format(r.EndOfSupport)})"));
    private static string Observed(List<(DotNetRelease Release, string Excerpt)> mentions)
        => $"Text mentions '{string.Join("', '", mentions.Select(x => x.Excerpt).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}'.";
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string DaysLeft(int days) => days switch { 0 => "today is the last day", 1 => "1 day left", _ => $"{days} days left" };
}

public sealed class MissingRollbackPlanRule : SafetyRuleBase
{
    public override string RuleId => "SR-OPS-001"; public override string Category => "Operability";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        if (RuleText.Has(text, @"\b(roll[- ]?back|revert|back[- ]?out|undo plan|reversib)")) yield break;
        if (input.Adr.Status is AdrStatus.Draft && text.Length < 200) yield break; // too early to judge
        yield return Build(create, "No rollback or back-out plan is described", FindingSeverity.Medium, 0.5, "No mention of rollback, revert or back-out in any section.", "If the decision fails in production there is no documented way back.", "Add a Rollback section describing triggers, steps, data implications and owners.",
            new[] { "Operability", "Availability" }, new[] { "Describe how to revert configuration/infrastructure and data.", "Define rollback criteria and who decides." });
    }
}

public sealed class MissingMigrationPlanRule : SafetyRuleBase
{
    public override string RuleId => "SR-OPS-002"; public override string Category => "Operability";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var trigger = RuleText.First(text, @"\b(migrat\w*|replac\w+ (?:the )?(?:existing|current|legacy)|move (?:away )?from|switch(?:ing)? from|decommission|sunset|re-?platform)\b");
        if (trigger is null) yield break;
        if (RuleText.Has(text, @"\b(migration plan|migration strategy|phase[sd]?|cut-?over|dual[- ]write|strangler|parallel run|pilot|incremental|wave \d|step \d)\b")) yield break;
        yield return Build(create, "Decision implies a migration but no migration plan is described", FindingSeverity.Medium, 0.5, $"Text mentions '{trigger}'.", "Replacing or moving systems without a phased plan risks outages and data inconsistency.", "Add a Migration section: phases, cut-over criteria, data migration/verification, parallel-run and rollback.",
            new[] { "Availability", "Data integrity", "Operability" }, new[] { "Use strangler-fig or parallel-run patterns.", "Define verification checkpoints per phase." });
    }
}

public sealed class WeakEvidenceRule : SafetyRuleBase
{
    public override string RuleId => "SR-EVD-001"; public override string Category => "WeakEvidence";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var a = input.Adr;
        if (a.Human.Rationale.Trim().Length < 80)
            yield return Build(create, "Rationale is missing or very short", FindingSeverity.Medium, 0.6, $"Rationale has {a.Human.Rationale.Trim().Length} characters.", "The decision cannot be evaluated or revisited without its reasoning.", "Document decision drivers, trade-offs and why the chosen option beats the alternatives.", new[] { "Maintainability", "Governance" }, new[] { "List decision drivers explicitly." });
        if (a.Human.Alternatives.Count == 0)
            yield return Build(create, "No considered alternatives are recorded", FindingSeverity.Medium, 0.6, "Alternatives list is empty.", "A single-option decision suggests the solution space was not explored.", "Record at least two rejected alternatives with the reason each was rejected.", new[] { "Governance" }, new[] { "Add a Considered Alternatives section." });
        if (a.Id != "inline" && a.Observed.Evidence.Count == 0 && a.Status is AdrStatus.Accepted or AdrStatus.UnderReview)
            yield return Build(create, "No implementation evidence is attached", FindingSeverity.Low, 0.5, "Evidence list is empty.", "Drift detection cannot verify the decision without evidence.", "Upload manifests, IaC or configuration via file_upload_request and attach them.", new[] { "Verifiability" }, new[] { "Attach the repository's dependency and infrastructure files." });
    }
}

public sealed class SecurityWeaknessRule : SafetyRuleBase
{
    private static readonly (string Pattern, string Issue, string Fix)[] Patterns =
    {
        (@"\bbasic auth(?:entication)?\b", "Basic authentication", "Use OAuth 2.0/OIDC with Entra ID or managed identities."),
        (@"\b(?:shared|account|storage) keys?\b(?!\s+(?:access\s+)?(?:disabled|is disabled|are disabled|must be disabled))", "Shared/account key authentication", "Use managed identity with RBAC and disable shared key access."),
        (@"\b(?:plain[- ]?text|unencrypted|clear[- ]?text)\s+(?:password|secret|credential|token|key)s?\b", "Plaintext secrets", "Store secrets in Key Vault and reference them at runtime."),
        (@"\bdisable\w*\s+(?:tls|ssl|certificate (?:validation|verification)|https)\b", "Disabled transport security", "Keep TLS enabled; fix the certificate chain instead."),
        (@"\bhttp://(?!localhost|127\.0\.0\.1)", "Non-TLS endpoint", "Use HTTPS only."),
        (@"\b(?:public|anonymous)\s+(?:blob|container|read)\s+access\b", "Public blob/container access", "Disable public access; use SAS or RBAC."),
        (@"\b(?:0\.0\.0\.0/0|allow all (?:ips|traffic|inbound)|any[- ]to[- ]any)\b", "Unrestricted network access", "Restrict with NSGs, private endpoints and IP allow-lists."),
        (@"\b(?:sa|root|admin)\s+(?:account|user|login)\b", "Privileged shared account", "Use least-privilege identities and RBAC."),
        (@"\bhard[- ]?cod\w+\s+(?:secret|password|key|credential|connection string)s?\b", "Hard-coded credentials", "Move to Key Vault and managed identity."),
        (@"\bsas\s+tokens?\b(?!.{0,60}\b(?:short|expir|user delegation|minimum))", "Long-lived SAS tokens", "Use user-delegation SAS with minutes-long expiry, or RBAC."),
        (@"\bwithout\s+(?:authentication|authorization|auth)\b|\bno\s+(?:authentication|authorization)\b|\banonymous\s+(?:access|endpoint)s?\b", "Unauthenticated access", "Require authentication at the edge (built-in auth / Entra ID)."),
    };
    public override string RuleId => "SR-SEC-001"; public override string Category => "Security";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.DecisionAndConsequences(input.Adr) + "\n" + input.Adr.Human.Title;
        foreach (var (pattern, issue, fix) in Patterns)
        {
            var m = RuleText.First(text, pattern);
            if (m is null) continue;
            yield return Build(create, $"Potential security weakness: {issue}", FindingSeverity.High, 0.55, $"Text mentions '{m}'.", $"{issue} weakens the security posture if adopted as described.", fix, new[] { "Security", "Compliance" }, new[] { fix, "Request a security review before acceptance." });
        }
    }
}

public sealed class AvailabilityAndDataLossRule : SafetyRuleBase
{
    public override string RuleId => "SR-AVL-001"; public override string Category => "Availability";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var single = RuleText.First(text, @"\b(single[- ](?:region|instance|node|server|az|availability zone|point of failure)|no (?:redundancy|failover|replica)|LRS\b)");
        if (single is not null && RuleText.Has(text, @"\bprod(?:uction)?\b") && !RuleText.Has(text, @"\b(accept(?:ed|able) risk|non-critical|dev(?:elopment)? only|tier 3)\b"))
            yield return Build(create, "Single point of failure in a production context", FindingSeverity.Medium, 0.5, $"Text mentions '{single}' together with production.", "A regional or instance failure would cause an outage.", "Use zone-redundant SKUs/ZRS and define RTO/RPO.", new[] { "Availability", "Resilience" }, new[] { "Enable zone redundancy.", "Document RTO/RPO and failover procedure." });
        var loss = RuleText.First(text, @"\b(without (?:a )?backups?|no backups?|disable\w* (?:soft[- ]delete|backups?|point[- ]in[- ]time|versioning)|retention (?:of )?0|delete\w* (?:all )?(?:data|records) (?:permanently|immediately)|truncat\w+ (?:the )?table)\b");
        if (loss is not null)
            yield return Build(create, "Potential data-loss risk", FindingSeverity.High, 0.6, $"Text mentions '{loss}'.", "Data could be lost without a recovery path.", "Keep backups/soft delete enabled and define retention and restore testing.", new[] { "Durability", "Recoverability" }, new[] { "Enable soft delete and point-in-time restore.", "Test restores periodically." });
    }
}

public sealed class PrivacyAndComplianceRule : SafetyRuleBase
{
    public override string RuleId => "SR-PRV-001"; public override string Category => "Privacy";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var pii = RuleText.First(text, @"\b(PII|personal data|personally identifiable|PHI|health (?:data|records?)|patient|credit card|card ?holder|PCI(?:[- ]DSS)?|biometric|SSN|social security)\b");
        if (pii is not null && !RuleText.Has(text, @"\b(encrypt\w*|mask\w*|tokeni[sz]\w*|pseudonym\w*|anonymi[sz]\w*|consent|retention|data classification|DPIA|GDPR|HIPAA|SOC ?2|ISO ?27001)\b"))
            yield return Build(create, "Sensitive data is in scope but no protection controls are described", FindingSeverity.Medium, 0.55, $"Text mentions '{pii}'.", "Handling regulated data without stated controls risks privacy breaches and non-compliance.", "Describe classification, encryption, masking, retention, access control and the applicable regime (GDPR/HIPAA/PCI).", new[] { "Privacy", "Compliance", "Security" }, new[] { "Add a data-protection section.", "Run a DPIA where required." });
        var waiver = RuleText.First(text, @"\b(not compliant|non-?compliant|compliance (?:exception|waiver|gap)|out of compliance)\b");
        if (waiver is not null)
            yield return Build(create, "Decision acknowledges a compliance gap", FindingSeverity.High, 0.65, $"Text mentions '{waiver}'.", "An explicit compliance gap needs a documented exception with owner and expiry.", "Record the exception, its approver, compensating controls and an expiry date.", new[] { "Compliance" }, new[] { "Create a tracked exception.", "Add compensating controls." });
        var residency = RuleText.First(text, @"\b(data (?:must|shall) (?:remain|reside|stay) in|data residency|sovereign)\b");
        if (residency is not null && RuleText.Has(text, @"\b(global|multi-?region|any region|worldwide)\b"))
            yield return Build(create, "Data-residency requirement may conflict with multi-region wording", FindingSeverity.Medium, 0.45, $"Text mentions '{residency}' and multi-region/global terms.", "Replication across regions may violate residency constraints.", "State the allowed regions explicitly and constrain replication.", new[] { "Compliance", "Privacy" }, new[] { "Pin regions in IaC and add a fitness function." });
    }
}

public sealed class VendorLockInAndCostRule : SafetyRuleBase
{
    public override string RuleId => "SR-ECO-001"; public override string Category => "Economics";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var proprietary = RuleText.AllMatches(text, @"\b(Cosmos DB|DynamoDB|Spanner|Lambda|Azure Functions|Step Functions|Logic Apps|BigQuery|Snowflake|Databricks|Aurora|Firebase|Durable Functions|Event Grid|Kinesis)\b").Take(3).ToList();
        if (proprietary.Count > 0 && !RuleText.Has(text, @"\b(lock-?in|exit strategy|portab\w+|abstraction|adapter|vendor[- ]neutral|open standard|multi-?cloud|migration path)\b"))
            yield return Build(create, "Proprietary managed services chosen without a lock-in assessment", FindingSeverity.Low, 0.45, $"Mentions {string.Join(", ", proprietary)}.", "Lock-in may be acceptable, but it should be a conscious trade-off.", "Add a sentence on exit strategy or why lock-in is acceptable.", new[] { "Portability", "Cost" }, new[] { "Isolate provider-specific code behind adapters.", "Record the exit cost estimate." });
        var pricey = RuleText.First(text, @"\b(premium (?:tier|sku|plan)|dedicated (?:tier|plan|cluster|hosts?)|provisioned throughput|reserved (?:capacity|instances?)|GPU|isolated (?:tier|plan)|business critical|hyperscale|multi-?master|multi-?region writes?)\b");
        if (pricey is not null && !RuleText.Has(text, @"\b(cost|budget|estimate|pricing|\$\d|€\d|£\d|FinOps|TCO)\b"))
            yield return Build(create, "High-cost tier selected without a cost estimate", FindingSeverity.Low, 0.5, $"Text mentions '{pricey}'.", "Expensive tiers without budget justification create cost exposure.", "Add a monthly cost estimate and the scaling model.", new[] { "Cost" }, new[] { "Run the pricing calculator and record assumptions.", "Set budget alerts." });
    }
}

public sealed class ScalabilityObservabilityRule : SafetyRuleBase
{
    public override string RuleId => "SR-SCL-001"; public override string Category => "Scalability";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var scale = RuleText.First(text, @"\b(single (?:server|vm|virtual machine|database for all|shared database)|vertical(?:ly)? scal\w+ only|scale[- ]up only|monolithic database|one database for all services|global lock|sticky sessions)\b");
        if (scale is not null)
            yield return Build(create, "Potential scalability limitation", FindingSeverity.Medium, 0.5, $"Text mentions '{scale}'.", "The design may not scale horizontally under load.", "Document expected load, scaling model and limits; prefer horizontally scalable components.", new[] { "Scalability", "Performance" }, new[] { "Define capacity targets.", "Load-test before acceptance." });
        if (input.Adr.Status is AdrStatus.Accepted or AdrStatus.UnderReview or AdrStatus.Proposed && !RuleText.Has(RuleText.DecisionAndConsequences(input.Adr), @"\b(logging|logs|monitor\w*|telemetry|alert\w*|observab\w*|tracing|metrics|Application Insights|OpenTelemetry|dashboards?|SLOs?|SLIs?)\b"))
            yield return Build(create, "No observability or operational monitoring is described", FindingSeverity.Low, 0.45, "Decision/consequences do not mention logging, monitoring, tracing, alerts or SLOs.", "Operability gaps surface after go-live.", "Add how the solution is monitored, alerted and traced, and which SLOs apply.", new[] { "Operability", "Observability" }, new[] { "Define SLIs/SLOs and alert rules.", "Emit structured logs and traces." });
    }
}

public sealed class IrreversibilityAndBlastRadiusRule : SafetyRuleBase
{
    public override string RuleId => "SR-RSK-001"; public override string Category => "Risk";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        var irreversible = RuleText.First(text, @"\b(irreversible|one[- ]way door|cannot be (?:undone|reversed)|permanent(?:ly)?|no way back|drop (?:the )?(?:schema|table|database)|delete (?:the )?(?:account|subscription|tenant))\b");
        if (irreversible is not null)
            yield return Build(create, "Decision appears irreversible", FindingSeverity.Medium, 0.55, $"Text mentions '{irreversible}'.", "Irreversible choices deserve extra scrutiny and explicit approval.", "Require named approvers, a pre-mortem, and staged execution with checkpoints.", new[] { "Risk", "Governance" }, new[] { "Add a pre-mortem.", "Stage the change with go/no-go gates." });
        var blast = RuleText.First(text, @"\b(all (?:services|teams|applications|tenants|customers)|every (?:service|team|application)|organi[sz]ation[- ]wide|platform[- ]wide|company[- ]wide|global (?:change|rollout)|big[- ]bang)\b");
        if (blast is not null)
            yield return Build(create, "Large blast radius", FindingSeverity.Medium, 0.5, $"Text mentions '{blast}'.", "A failure would affect many consumers at once.", "Roll out incrementally (canary/rings) with kill switches and clear ownership.", new[] { "Availability", "Risk" }, new[] { "Use ring-based rollout.", "Define a kill switch." });
    }
}

public sealed class ResponsibleAiAccessibilityRule : SafetyRuleBase
{
    public override string RuleId => "SR-RAI-001"; public override string Category => "ResponsibleAI";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var text = RuleText.All(input.Adr);
        if (RuleText.Has(text, @"\b(LLM|large language model|generative AI|GPT|Claude|Copilot|machine learning|ML model|AI agent|prompt)\b") && !RuleText.Has(text, @"\b(responsible AI|human[- ]in[- ]the[- ]loop|human review|bias|fairness|content safety|hallucinat\w*|grounding|evaluation|red[- ]team|guardrails?)\b"))
            yield return Build(create, "AI capability without responsible-AI controls", FindingSeverity.Low, 0.5, "Text mentions AI/ML without human review, evaluation or content-safety controls.", "Unreviewed model output may produce harmful or incorrect results.", "Describe human oversight, evaluation, content safety and prompt-injection defences.", new[] { "Safety", "Trust" }, new[] { "Add human-in-the-loop for consequential actions.", "Add evaluation and red-teaming." });
        if (RuleText.Has(text, @"\b(user interface|UI|front[- ]?end|web app|portal|mobile app)\b") && !RuleText.Has(text, @"\b(accessib\w*|WCAG|a11y|screen reader|keyboard)\b"))
            yield return Build(create, "User-facing component without accessibility consideration", FindingSeverity.Info, 0.4, "UI mentioned without accessibility terms.", "Accessibility requirements may be missed.", "Reference WCAG 2.2 AA and the testing approach.", new[] { "Accessibility" }, new[] { "Add WCAG conformance target." });
    }
}

/// <summary>Conflicts with accepted decisions: reuses the contradiction rules against active ADRs.</summary>
public sealed class ConflictsWithAcceptedRule : SafetyRuleBase
{
    private readonly IEnumerable<IContradictionRule> _rules;
    private readonly IClaimExtractor _claims;
    public ConflictsWithAcceptedRule(IEnumerable<IContradictionRule> rules, IClaimExtractor claims) { _rules = rules; _claims = claims; }
    public override string RuleId => "SR-CON-001"; public override string Category => "Consistency";
    public override IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create)
    {
        var mine = input.Adr.Human.Claims.Concat(_claims.Extract(input.Adr)).ToList();
        foreach (var other in input.OtherActiveAdrs)
        {
            if (other.Human.Supersedes == input.Adr.Id || input.Adr.Human.Supersedes == other.Id) continue;
            var theirs = other.Human.Claims.Concat(_claims.Extract(other)).ToList();
            foreach (var c in _rules.SelectMany(r => r.Match(input.Adr, mine, other, theirs)).Take(5))
                yield return Build(create, $"Conflicts with accepted {other.Id}: {c.ContradictionType}", c.DeterministicScore >= 0.85 ? FindingSeverity.High : FindingSeverity.Medium, c.DeterministicScore, c.DeterministicExplanation, "Accepting this ADR without superseding or scoping would leave two binding, conflicting decisions.", $"Either scope this decision explicitly (system/environment/context), supersede {other.Id}, or record an accepted exception.", new[] { "Consistency", "Governance" }, new[] { "Run adr_check_contradictions and classify the result.", $"Consider adr_supersede for {other.Id}." });
        }
    }
}
