using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Errors;

namespace AdrMcp.UnitTests.Domain;

public class LifecycleTests
{
    [Theory]
    [InlineData(AdrStatus.Draft, AdrStatus.Proposed, true)]
    [InlineData(AdrStatus.Proposed, AdrStatus.UnderReview, true)]
    [InlineData(AdrStatus.UnderReview, AdrStatus.Accepted, true)]
    [InlineData(AdrStatus.Accepted, AdrStatus.Superseded, true)]
    [InlineData(AdrStatus.Accepted, AdrStatus.Deprecated, true)]
    [InlineData(AdrStatus.Draft, AdrStatus.Accepted, false)]
    [InlineData(AdrStatus.Accepted, AdrStatus.Draft, false)]
    [InlineData(AdrStatus.Archived, AdrStatus.Draft, false)]
    [InlineData(AdrStatus.Superseded, AdrStatus.Accepted, false)]
    [InlineData(AdrStatus.Rejected, AdrStatus.Draft, true)]
    public void Transitions_follow_the_state_machine(AdrStatus from, AdrStatus to, bool allowed)
    {
        Assert.Equal(allowed, AdrLifecycle.CanTransition(from, to));
        if (!allowed) Assert.Throws<InvalidTransitionException>(() => AdrLifecycle.EnsureTransition(from, to));
    }

    [Fact]
    public void Archived_is_terminal()
    {
        Assert.Empty(AdrLifecycle.AllowedNext(AdrStatus.Archived));
        Assert.True(AdrLifecycle.IsTerminal(AdrStatus.Archived));
        Assert.True(AdrLifecycle.IsTerminal(AdrStatus.Superseded));
    }
}

public class ClaimScopeTests
{
    [Fact]
    public void Global_scope_overlaps_everything() => Assert.True(ClaimScope.Global.Overlaps(new ClaimScope(System: "Orders", Environment: "production")));

    [Fact]
    public void Different_systems_do_not_overlap() => Assert.False(new ClaimScope(System: "Orders").Overlaps(new ClaimScope(System: "Billing")));

    [Fact]
    public void Different_environments_do_not_overlap() => Assert.False(new ClaimScope(Environment: "production").Overlaps(new ClaimScope(Environment: "development")));

    [Fact]
    public void Disjoint_date_ranges_do_not_overlap()
    {
        var a = new ClaimScope(EffectiveFrom: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), EffectiveTo: new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var b = new ClaimScope(EffectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.False(a.Overlaps(b));
        Assert.True(a.Overlaps(new ClaimScope(EffectiveFrom: new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero))));
    }
}
