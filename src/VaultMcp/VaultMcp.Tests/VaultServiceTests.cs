using VaultMcp.Core;
using Xunit;

namespace VaultMcp.Tests;

public sealed class VaultServiceTests : IDisposable
{
    private readonly TempVault _vault = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Search_ranks_a_title_match_first_and_reports_matched_terms()
    {
        var result = await _vault.Service().SearchAsync("naming convention", null, _ct);
        Assert.Equal("Work/Naming convention.md", result.Hits[0].Path);
        Assert.Equal("Azure naming convention", result.Hits[0].Title);
        Assert.Equal(new[] { "naming", "convention" }, result.Hits[0].MatchedTerms);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Search_honours_take_and_the_configured_default()
    {
        var svc = _vault.Service();
        Assert.Single((await svc.SearchAsync("cosmos", 1, _ct)).Hits);
        var all = await svc.SearchAsync("cosmos", null, _ct);
        Assert.True(all.Hits.Count <= 5, "max_results in adr-sidekick.md is 5");
        Assert.Equal(all.Total, all.Hits.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  , . ")]
    public async Task Search_refuses_an_empty_query(string query)
    {
        var ex = await Assert.ThrowsAsync<VaultException>(() => _vault.Service().SearchAsync(query, null, _ct));
        Assert.Equal("INVALID_QUERY", ex.Code);
    }

    [Fact]
    public async Task Get_by_path_or_unique_name_returns_title_tags_links_and_excerpt()
    {
        var svc = _vault.Service();
        var byPath = await svc.GetNoteAsync("Work/Cosmos decision.md", null, _ct);
        var byName = await svc.GetNoteAsync("cosmos decision", null, _ct);
        Assert.Equal(byPath.Path, byName.Path);
        Assert.Equal("Cosmos DB over Table Storage", byPath.Title);
        Assert.Equal(new[] { "adr-context", "azure/cosmos" }, byPath.Tags);
        Assert.Equal(new[] { "Naming convention", "Personal/Diary" }, byPath.Links);
        Assert.False(byPath.Truncated);
        Assert.DoesNotContain("---", byPath.Excerpt);
    }

    [Fact]
    public async Task Get_truncates_at_the_requested_length_and_says_so()
    {
        var note = await _vault.Service().GetNoteAsync("Work/Naming convention.md", 100, _ct);
        Assert.True(note.Truncated);
        Assert.True(note.Excerpt.Length <= 102, note.Excerpt);
        Assert.EndsWith("…", note.Excerpt);
        Assert.True(note.TotalChars > 100);
    }

    [Fact]
    public async Task Get_by_an_ambiguous_name_asks_for_the_path()
    {
        _vault.Write("Projects/Cloud/Naming convention.md", "another one");
        var ex = await Assert.ThrowsAsync<VaultException>(() => _vault.Service().GetNoteAsync("Naming convention", null, _ct));
        Assert.Equal("AMBIGUOUS", ex.Code);
        Assert.Contains("Work/Naming convention.md", ex.Message);
    }

    [Fact]
    public async Task Backlinks_follow_name_links_alias_links_and_path_links()
    {
        var svc = _vault.Service();
        var cosmos = await svc.BacklinksAsync("Cosmos decision", null, _ct);
        Assert.Equal(new[] { "Projects/Cloud/Landing zone.md" }, cosmos.Notes.Select(n => n.Path));
        var naming = await svc.BacklinksAsync("Work/Naming convention.md", null, _ct);
        Assert.Equal(new[] { "Projects/Cloud/Landing zone.md", "Work/Cosmos decision.md" }, naming.Notes.Select(n => n.Path));
    }

    [Fact]
    public async Task Tags_default_to_the_context_tag_match_nested_tags_and_ignore_code()
    {
        var svc = _vault.Service();
        var context = await svc.TagsAsync(null, null, _ct);
        Assert.Equal("#adr-context", context.Subject);
        Assert.Equal(2, context.Total);
        Assert.DoesNotContain(context.Notes, n => n.Path.Contains("Personal", StringComparison.Ordinal));

        var azure = await svc.TagsAsync("#azure", null, _ct);
        Assert.Equal(2, azure.Total);

        Assert.Equal(0, (await svc.TagsAsync("notatag", null, _ct)).Total);
        Assert.Equal(0, (await svc.BacklinksAsync("Cosmos decision", null, _ct)).Notes.Count(n => n.Path.Contains("Not a link", StringComparison.Ordinal)));
    }

    public void Dispose() => _vault.Dispose();
}
