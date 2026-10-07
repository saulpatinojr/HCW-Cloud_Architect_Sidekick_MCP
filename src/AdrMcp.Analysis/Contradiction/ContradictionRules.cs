using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Analysis.Contradiction;

internal static class RuleHelpers
{
    public static bool SameTech(DecisionClaim a, DecisionClaim b)
    {
        var x = a.NormalizedSubject; var y = b.NormalizedSubject;
        if (x == y) return true;
        var xs = Simplify(x); var ys = Simplify(y);
        return xs == ys || (xs.Length >= 4 && ys.Length >= 4 && (xs.Contains(ys) || ys.Contains(xs)));
    }
    public static string Simplify(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());
    public static bool ScopesOverlap(DecisionClaim a, DecisionClaim b) => a.Scope.Overlaps(b.Scope);
    public static string ScopeText(ClaimScope s) => string.Join(", ", new[] { s.System is null ? null : $"system={s.System}", s.BoundedContext is null ? null : $"context={s.BoundedContext}", s.Environment is null ? null : $"env={s.Environment}" }.Where(x => x is not null)) is { Length: > 0 } t ? t : "global";
}

/// <summary>A prohibits X while B requires X in an overlapping scope.</summary>
public sealed class ProhibitedVsRequiredRule : IContradictionRule
{
    public string Name => "ProhibitedVsRequired";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var (p, r, pa, ra) in ca.Where(c => c.Kind == ClaimKind.ProhibitedTechnology).SelectMany(p => cb.Where(c => c.Kind == ClaimKind.RequiredTechnology).Select(r => (p, r, a, b)))
                 .Concat(cb.Where(c => c.Kind == ClaimKind.ProhibitedTechnology).SelectMany(p => ca.Where(c => c.Kind == ClaimKind.RequiredTechnology).Select(r => (p, r, b, a)))))
        {
            if (!RuleHelpers.SameTech(p, r)) continue;
            if (!RuleHelpers.ScopesOverlap(p, r)) continue; // different system/env/context/date: not a contradiction
            var exact = p.NormalizedSubject == r.NormalizedSubject;
            yield return new ContradictionCandidate(pa, p, ra, r, Name,
                $"{pa.Id} prohibits '{p.Subject}' ({RuleHelpers.ScopeText(p.Scope)}) while {ra.Id} requires '{r.Subject}' ({RuleHelpers.ScopeText(r.Scope)}).",
                exact ? 0.95 : 0.7);
        }
    }
}

/// <summary>Synchronous vs asynchronous integration for the same scope.</summary>
public sealed class IntegrationStyleRule : IContradictionRule
{
    public string Name => "IntegrationStyleConflict";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var x in ca.Where(c => c.Kind == ClaimKind.IntegrationStyle))
            foreach (var y in cb.Where(c => c.Kind == ClaimKind.IntegrationStyle))
            {
                if (x.NormalizedObject is null || y.NormalizedObject is null || x.NormalizedObject == y.NormalizedObject) continue;
                if (!RuleHelpers.SameTech(x, y) || !RuleHelpers.ScopesOverlap(x, y)) continue;
                yield return new ContradictionCandidate(a, x, b, y, Name, $"{a.Id} mandates {x.Object} integration while {b.Id} mandates {y.Object} integration for an overlapping scope ({RuleHelpers.ScopeText(x.Scope)} / {RuleHelpers.ScopeText(y.Scope)}).", 0.9);
            }
    }
}

/// <summary>Two different services claim exclusive ownership of the same database.</summary>
public sealed class DatabaseOwnershipRule : IContradictionRule
{
    public string Name => "DatabaseOwnershipConflict";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var x in ca.Where(c => c.Kind == ClaimKind.DatabaseOwnership && c.NormalizedObject is not null))
            foreach (var y in cb.Where(c => c.Kind == ClaimKind.DatabaseOwnership && c.NormalizedObject is not null))
            {
                if (RuleHelpers.Simplify(x.NormalizedObject!) != RuleHelpers.Simplify(y.NormalizedObject!)) continue;
                if (RuleHelpers.Simplify(x.NormalizedSubject) == RuleHelpers.Simplify(y.NormalizedSubject)) continue;
                if (!RuleHelpers.ScopesOverlap(x, y)) continue;
                yield return new ContradictionCandidate(a, x, b, y, Name, $"{a.Id} assigns database '{x.Object}' to '{x.Subject}' while {b.Id} assigns it to '{y.Subject}'.", 0.95);
            }
    }
}

/// <summary>Different cloud providers / deployment targets for an overlapping scope.</summary>
public sealed class CloudProviderRule : IContradictionRule
{
    public string Name => "CloudProviderConflict";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var x in ca.Where(c => c.Kind == ClaimKind.CloudProvider && c.NormalizedObject is not null))
            foreach (var y in cb.Where(c => c.Kind == ClaimKind.CloudProvider && c.NormalizedObject is not null))
            {
                if (x.NormalizedObject == y.NormalizedObject || !RuleHelpers.ScopesOverlap(x, y)) continue;
                yield return new ContradictionCandidate(a, x, b, y, Name, $"{a.Id} targets {x.Object} while {b.Id} targets {y.Object} ({RuleHelpers.ScopeText(x.Scope)} / {RuleHelpers.ScopeText(y.Scope)}).", 0.85);
            }
    }
}

/// <summary>Different authentication strategies for the same scope.</summary>
public sealed class AuthStrategyRule : IContradictionRule
{
    public string Name => "AuthStrategyConflict";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var x in ca.Where(c => c.Kind == ClaimKind.AuthStrategy && c.NormalizedObject is not null))
            foreach (var y in cb.Where(c => c.Kind == ClaimKind.AuthStrategy && c.NormalizedObject is not null))
            {
                if (x.NormalizedObject == y.NormalizedObject || !RuleHelpers.ScopesOverlap(x, y)) continue;
                // Entra ID and OAuth2/OIDC are compatible families; flag as low-score candidate for semantic review instead of a hard conflict.
                var compatible = new HashSet<string> { "entra-id", "oauth2-oidc", "managed-identity" };
                var score = compatible.Contains(x.NormalizedObject!) && compatible.Contains(y.NormalizedObject!) ? 0.4 : 0.8;
                yield return new ContradictionCandidate(a, x, b, y, Name, $"{a.Id} authenticates with {x.Object} while {b.Id} authenticates with {y.Object} for an overlapping scope.", score);
            }
    }
}

/// <summary>Conflicting data-residency regions for overlapping scope.</summary>
public sealed class DataResidencyRule : IContradictionRule
{
    public string Name => "DataResidencyConflict";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var x in ca.Where(c => c.Kind == ClaimKind.DataResidency && c.NormalizedObject is not null))
            foreach (var y in cb.Where(c => c.Kind == ClaimKind.DataResidency && c.NormalizedObject is not null))
            {
                if (x.NormalizedObject == y.NormalizedObject || !RuleHelpers.ScopesOverlap(x, y)) continue;
                if (IsSubRegion(x.NormalizedObject!, y.NormalizedObject!) || IsSubRegion(y.NormalizedObject!, x.NormalizedObject!)) continue; // EU vs Germany is compatible
                yield return new ContradictionCandidate(a, x, b, y, Name, $"{a.Id} requires data in {x.Object} while {b.Id} requires data in {y.Object}.", 0.85);
            }
    }
    private static bool IsSubRegion(string parent, string child) => (parent is "eu" or "europe" or "european union") && child is "germany" or "france" or "netherlands" or "ireland" or "sweden" or "europe" or "eu" or "european union";
}

/// <summary>ADR text vs observed implementation: A prohibits a technology that B's evidence shows as observed.</summary>
public sealed class ProhibitedVsObservedRule : IContradictionRule
{
    public string Name => "ProhibitedVsObservedImplementation";
    public IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> ca, Adr b, IReadOnlyList<DecisionClaim> cb)
    {
        foreach (var (p, src, other) in ca.Where(c => c.Kind == ClaimKind.ProhibitedTechnology).Select(p => (p, a, b)).Concat(cb.Where(c => c.Kind == ClaimKind.ProhibitedTechnology).Select(p => (p, b, a))))
        {
            var simplified = RuleHelpers.Simplify(p.NormalizedSubject);
            if (simplified.Length < 3) continue;
            var observed = other.Observed.DetectedTechnologies.FirstOrDefault(t => t.Status == TechnologyStatus.Observed && (RuleHelpers.Simplify(t.NormalizedName.ToLowerInvariant()).Contains(simplified) || simplified.Contains(RuleHelpers.Simplify(t.NormalizedName.ToLowerInvariant()))));
            if (observed is null) continue;
            var pseudo = new DecisionClaim($"obs-{observed.Key.GetHashCode():x}", ClaimKind.RequiredTechnology, observed.NormalizedName, "observed in", observed.SourceFile, ClaimScope.Global, $"Observed {observed.NormalizedName} {observed.DetectedVersion} in {observed.SourceFile}", Declared: false);
            yield return new ContradictionCandidate(src, p, other, pseudo, Name, $"{src.Id} prohibits '{p.Subject}' but evidence linked to {other.Id} shows '{observed.NormalizedName}' in {observed.SourceFile}.", 0.8);
        }
    }
}

/// <summary>Default: no model-based pass. Candidates below the deterministic threshold are returned to the MCP client for review.</summary>
public sealed class NullSemanticContradictionAnalyzer : ISemanticContradictionAnalyzer
{
    public bool IsEnabled => false;
    public Task<(bool IsContradiction, double Confidence, string Explanation)?> AnalyzeAsync(ContradictionCandidate candidate, CancellationToken ct) => Task.FromResult<(bool, double, string)?>(null);
}
