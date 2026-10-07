using AdrMcp.Domain.Errors;

namespace AdrMcp.Domain.Adrs;

public enum AdrStatus
{
    Draft,
    Proposed,
    UnderReview,
    Accepted,
    Rejected,
    Deprecated,
    Superseded,
    Archived
}

/// <summary>Lifecycle rules. Status is human-owned; the system never changes it.</summary>
public static class AdrLifecycle
{
    private static readonly IReadOnlyDictionary<AdrStatus, AdrStatus[]> Transitions = new Dictionary<AdrStatus, AdrStatus[]>
    {
        [AdrStatus.Draft] = new[] { AdrStatus.Proposed, AdrStatus.Archived },
        [AdrStatus.Proposed] = new[] { AdrStatus.UnderReview, AdrStatus.Draft, AdrStatus.Rejected, AdrStatus.Archived },
        [AdrStatus.UnderReview] = new[] { AdrStatus.Accepted, AdrStatus.Rejected, AdrStatus.Proposed, AdrStatus.Archived },
        [AdrStatus.Accepted] = new[] { AdrStatus.Deprecated, AdrStatus.Superseded, AdrStatus.Archived },
        [AdrStatus.Rejected] = new[] { AdrStatus.Draft, AdrStatus.Archived },
        [AdrStatus.Deprecated] = new[] { AdrStatus.Superseded, AdrStatus.Archived },
        [AdrStatus.Superseded] = new[] { AdrStatus.Archived },
        [AdrStatus.Archived] = Array.Empty<AdrStatus>(),
    };

    /// <summary>Statuses whose decisions are considered binding for contradiction and drift analysis.</summary>
    public static readonly IReadOnlySet<AdrStatus> ActiveStatuses = new HashSet<AdrStatus> { AdrStatus.Accepted, AdrStatus.Deprecated };

    public static bool CanTransition(AdrStatus from, AdrStatus to) => from != to && Transitions[from].Contains(to);

    public static void EnsureTransition(AdrStatus from, AdrStatus to)
    {
        if (!CanTransition(from, to)) throw new InvalidTransitionException(from.ToString(), to.ToString());
    }

    public static IReadOnlyList<AdrStatus> AllowedNext(AdrStatus from) => Transitions[from];

    /// <summary>Statuses that mean "no longer the current decision" and therefore need a supersededBy or archive reason.</summary>
    public static bool IsTerminal(AdrStatus s) => s is AdrStatus.Superseded or AdrStatus.Archived;
}
