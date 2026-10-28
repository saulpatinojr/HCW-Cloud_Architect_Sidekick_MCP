using VaultMcp.Core;
using Xunit;

namespace VaultMcp.Tests;

/// <summary>The allow-list is the only thing between the tools and personal notes. These tests pin it down.</summary>
public sealed class AllowListTests : IDisposable
{
    private readonly TempVault _vault = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Without_the_settings_note_nothing_is_readable()
    {
        using var bare = new TempVault(withConfig: false);
        var svc = bare.Service();
        var search = await svc.SearchAsync("cosmos", null, _ct);
        Assert.Equal(0, search.Total);
        Assert.Contains(VaultConfig.FileName, search.Note);
        var ex = await Assert.ThrowsAsync<VaultException>(() => svc.GetNoteAsync("Work/Cosmos decision.md", null, _ct));
        Assert.Equal("NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Search_returns_allow_listed_notes_only()
    {
        var hits = (await _vault.Service().SearchAsync("cosmos", 50, _ct)).Hits;
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.Path.StartsWith("Work/", StringComparison.Ordinal) || h.Path.StartsWith("Projects/Cloud/", StringComparison.Ordinal), h.Path));
        Assert.DoesNotContain(hits, h => h.Path.Contains("Personal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hits, h => h.Path.Contains(".obsidian", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hits, h => h.Path.Contains(".trash", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Personal/Diary.md")]
    [InlineData("Diary")]
    [InlineData("../Personal/Diary.md")]
    [InlineData("Work/../Personal/Diary.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData(".obsidian/plugins/x/notes.md")]
    [InlineData("adr-sidekick.md")]
    [InlineData("")]
    public async Task Get_refuses_anything_outside_the_allow_list_with_one_message(string path)
    {
        var ex = await Assert.ThrowsAsync<VaultException>(() => _vault.Service().GetNoteAsync(path, null, _ct));
        Assert.Equal("NOT_FOUND", ex.Code);
        Assert.DoesNotContain("Diary", ex.Message);
    }

    [Fact]
    public async Task Allow_list_folders_match_exactly_and_case_sensitively()
    {
        _vault.Write(VaultConfig.FileName, "---\nallow: [work, Workshop]\n---\n");
        _vault.Write("Workshop/Note.md", "cosmos in a sibling folder");
        var hits = (await _vault.Service().SearchAsync("cosmos", 50, _ct)).Hits;
        Assert.Single(hits);
        Assert.Equal("Workshop/Note.md", hits[0].Path);
    }

    [Fact]
    public async Task Hidden_and_climbing_allow_entries_are_dropped()
    {
        var cfg = VaultConfig.Parse("---\nallow:\n  - .obsidian\n  - ../outside\n  - /\n  - Work/\n---\n");
        Assert.Equal(new[] { "Work" }, cfg.AllowedFolders);
        _vault.Write(VaultConfig.FileName, "---\nallow:\n  - .obsidian\n  - ../outside\n---\n");
        Assert.Equal(0, (await _vault.Service().SearchAsync("cosmos", null, _ct)).Total);
    }

    [Fact]
    public async Task Store_never_reads_outside_the_root()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_vault.Root)!, Path.GetFileName(_vault.Root) + "-outside.md");
        File.WriteAllText(outside, "outside");
        try
        {
            var store = new LocalFolderVaultStore(_vault.Root);
            Assert.Null(await store.ReadAsync("../" + Path.GetFileName(outside), _ct));
            Assert.Null(store.Resolve("../" + Path.GetFileName(outside)));
            Assert.Null(store.Resolve(outside));
            Assert.NotNull(await store.ReadAsync("Work/Cosmos decision.md", _ct));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Changes_on_disk_show_up_after_the_index_ttl()
    {
        var svc = _vault.Service(TimeSpan.Zero);
        Assert.Equal(0, (await svc.SearchAsync("kubernetes", null, _ct)).Total);
        _vault.Write("Work/AKS.md", "# AKS\nkubernetes cluster sizing\n");
        Assert.Equal(1, (await svc.SearchAsync("kubernetes", null, _ct)).Total);
    }

    public void Dispose() => _vault.Dispose();
}
