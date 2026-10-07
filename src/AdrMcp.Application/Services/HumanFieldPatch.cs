using System.Text.Json;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Errors;

namespace AdrMcp.Application.Services;

/// <summary>Partial update of human-owned fields. Null means "unchanged".</summary>
public sealed record HumanFieldPatch
{
    public string? Title { get; init; }
    public string? Context { get; init; }
    public string? Decision { get; init; }
    public string? Rationale { get; init; }
    public IReadOnlyList<string>? Alternatives { get; init; }
    public string? Consequences { get; init; }
    public AdrStatus? Status { get; init; }
    public IReadOnlyList<string>? Owners { get; init; }
    public IReadOnlyList<string>? Approvers { get; init; }
    public string? ApprovalResult { get; init; }
    public string? HumanNotes { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<DecisionClaim>? Claims { get; init; }
    public string? Supersedes { get; init; }
    public string? SupersededBy { get; init; }

    public bool ChangesStatus => Status is not null;
    public bool ChangesSupersession => Supersedes is not null || SupersededBy is not null;

    public bool IsEmpty => Title is null && Context is null && Decision is null && Rationale is null && Alternatives is null && Consequences is null
        && Status is null && Owners is null && Approvers is null && ApprovalResult is null && HumanNotes is null && Tags is null && Claims is null && Supersedes is null && SupersededBy is null;

    public HumanOwnedContent ApplyTo(HumanOwnedContent c) => c with
    {
        Title = Title ?? c.Title,
        Context = Context ?? c.Context,
        Decision = Decision ?? c.Decision,
        Rationale = Rationale ?? c.Rationale,
        Alternatives = Alternatives ?? c.Alternatives,
        Consequences = Consequences ?? c.Consequences,
        Status = Status ?? c.Status,
        Owners = Owners ?? c.Owners,
        Approvers = Approvers ?? c.Approvers,
        ApprovalResult = ApprovalResult ?? c.ApprovalResult,
        HumanNotes = HumanNotes ?? c.HumanNotes,
        Tags = Tags ?? c.Tags,
        Claims = Claims ?? c.Claims,
        Supersedes = Supersedes ?? c.Supersedes,
        SupersededBy = SupersededBy ?? c.SupersededBy,
    };

    /// <summary>Builds a patch for one named human-owned field from a string value (plain text, or JSON for list/complex fields).</summary>
    public static HumanFieldPatch ForField(string field, string value)
    {
        if (!HumanOwnedFields.IsHumanOwned(field))
            throw new ValidationException($"'{field}' is not a human-owned field; system-managed fields are refreshed by analyzers and cannot be proposed.");
        return field.ToLowerInvariant() switch
        {
            HumanOwnedFields.Title => new() { Title = value },
            HumanOwnedFields.Context => new() { Context = value },
            HumanOwnedFields.Decision => new() { Decision = value },
            HumanOwnedFields.Rationale => new() { Rationale = value },
            HumanOwnedFields.Alternatives => new() { Alternatives = ParseList(value) },
            HumanOwnedFields.Consequences => new() { Consequences = value },
            "status" => new() { Status = Enum.TryParse<AdrStatus>(value, true, out var s) ? s : throw new ValidationException($"Unknown status '{value}'.") },
            HumanOwnedFields.Owners => new() { Owners = ParseList(value) },
            HumanOwnedFields.Approvers => new() { Approvers = ParseList(value) },
            "approvalresult" => new() { ApprovalResult = value },
            "humannotes" => new() { HumanNotes = value },
            HumanOwnedFields.Tags => new() { Tags = ParseList(value) },
            HumanOwnedFields.Claims => new() { Claims = JsonSerializer.Deserialize<List<DecisionClaim>>(value, AppJson.Options) ?? new List<DecisionClaim>() },
            HumanOwnedFields.Supersedes => new() { Supersedes = value },
            "supersededby" => new() { SupersededBy = value },
            _ => throw new ValidationException($"Unsupported field '{field}'.")
        };
    }

    public static string? CurrentValueOf(HumanOwnedContent c, string field) => field.ToLowerInvariant() switch
    {
        HumanOwnedFields.Title => c.Title,
        HumanOwnedFields.Context => c.Context,
        HumanOwnedFields.Decision => c.Decision,
        HumanOwnedFields.Rationale => c.Rationale,
        HumanOwnedFields.Alternatives => AppJson.Serialize(c.Alternatives),
        HumanOwnedFields.Consequences => c.Consequences,
        "status" => c.Status.ToString(),
        HumanOwnedFields.Owners => AppJson.Serialize(c.Owners),
        HumanOwnedFields.Approvers => AppJson.Serialize(c.Approvers),
        "approvalresult" => c.ApprovalResult,
        "humannotes" => c.HumanNotes,
        HumanOwnedFields.Tags => AppJson.Serialize(c.Tags),
        HumanOwnedFields.Claims => AppJson.Serialize(c.Claims),
        HumanOwnedFields.Supersedes => c.Supersedes,
        "supersededby" => c.SupersededBy,
        _ => null
    };

    private static IReadOnlyList<string> ParseList(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<List<string>>(trimmed, AppJson.Options) ?? new List<string>(); }
            catch (JsonException) { /* fall through to line parsing */ }
        }
        return trimmed.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.TrimStart('-', '*', ' ')).Where(s => s.Length > 0).ToList();
    }
}
