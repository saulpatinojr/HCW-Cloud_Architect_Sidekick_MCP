namespace AdrMcp.Application.Abstractions;

public enum SafetySeverity { Low, Medium, High, Critical }

public enum SafetyAction { None, Masked, Removed, Flagged, Blocked }

/// <summary>A detection. The matched text is never stored; only its type, location and a short, non-reversible fingerprint.</summary>
public sealed record SafetyDetection(
    string Type,
    SafetySeverity Severity,
    int Line,
    int Column,
    int Length,
    SafetyAction Action,
    string Detector,
    string? Fingerprint = null,
    string? Category = null);

public sealed record SafetyScanResult(
    string SanitizedText,
    IReadOnlyList<SafetyDetection> Detections,
    bool Blocked,
    IReadOnlyList<string> Limitations,
    bool PromptInjectionSuspected)
{
    public bool HasCritical => Detections.Any(d => d.Severity == SafetySeverity.Critical);
}

public sealed record SafetyScanOptions(
    string SourceName,
    bool BlockOnCritical,
    IReadOnlySet<string>? Allowlist = null,
    bool MaskPii = true);

/// <summary>
/// Deterministic-first content safety pipeline. Implementations may chain model-based services, but must label them as such.
/// Runs BEFORE any content is sent to an LLM or stored in a sanitized variant.
/// </summary>
public interface IContentSafetyService
{
    Task<SafetyScanResult> ScanAsync(string text, SafetyScanOptions options, CancellationToken ct);
    IReadOnlyList<string> DetectorNames { get; }
}

/// <summary>Supplies organization-defined patterns (name, regex, severity). Backed by configuration or Key Vault.</summary>
public interface ISensitivePatternProvider
{
    IReadOnlyList<(string Name, string Pattern, SafetySeverity Severity, string Category)> GetPatterns();
    IReadOnlySet<string> GetAllowlist();
}
