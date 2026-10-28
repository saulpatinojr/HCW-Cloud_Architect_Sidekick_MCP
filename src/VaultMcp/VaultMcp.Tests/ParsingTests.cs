using VaultMcp.Core;
using Xunit;

namespace VaultMcp.Tests;

public sealed class ParsingTests
{
    [Fact]
    public void Front_matter_reads_block_lists_inline_lists_quoted_scalars_and_ints()
    {
        var (front, body) = FrontMatter.Split("---\nallow:\n  - Work\n  - \"Projects/Cloud\"\ntags: [a, 'b c']\ntag: \"adr-context\"\nexcerpt_chars: 500\n# a comment\nignored\n---\nBody line\n");
        Assert.Equal(new[] { "Work", "Projects/Cloud" }, front.List("allow"));
        Assert.Equal(new[] { "a", "b c" }, front.List("tags"));
        Assert.Equal("adr-context", front.Scalar("tag"));
        Assert.Equal(500, front.Int("excerpt_chars"));
        Assert.Null(front.Int("tag"));
        Assert.Equal("Body line\n", body);
    }

    [Theory]
    [InlineData("No front matter\n---\nnot a block\n")]
    [InlineData("---\nunterminated: true\n")]
    [InlineData("")]
    public void Text_without_a_front_matter_block_is_all_body(string text)
    {
        var (front, body) = FrontMatter.Split(text);
        Assert.Same(FrontMatter.Empty, front);
        Assert.Equal(text, body);
    }

    [Fact]
    public void Config_defaults_and_clamps()
    {
        Assert.Same(VaultConfig.Empty, VaultConfig.Parse(null));
        var cfg = VaultConfig.Parse("---\nallow: [Work]\nexcerpt_chars: 5\nmax_results: 999\ntag: '#ctx'\n---\n");
        Assert.Equal(200, cfg.ExcerptChars);
        Assert.Equal(50, cfg.MaxResults);
        Assert.Equal("ctx", cfg.ContextTag);
        Assert.True(cfg.IsAllowed("Work/x.md"));
        Assert.False(cfg.IsAllowed("Workshop/x.md"));
        Assert.False(cfg.IsAllowed("Work.md"));
    }

    [Theory]
    [InlineData("Work/Note.md", "Work/Note.md")]
    [InlineData("Work\\Note.md", "Work/Note.md")]
    [InlineData(" Work//Note.md ", "Work/Note.md")]
    [InlineData("/Work/Note.md", null)]
    [InlineData("C:/Work/Note.md", null)]
    [InlineData("../Note.md", null)]
    [InlineData("Work/./Note.md", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Paths_normalise_or_are_refused(string? input, string? expected) => Assert.Equal(expected, VaultPath.Normalize(input));

    [Fact]
    public void Note_parser_takes_title_from_front_matter_then_h1_then_file_name()
    {
        var file = new VaultFile("Work/My note.md", DateTimeOffset.UnixEpoch);
        Assert.Equal("From front", NoteParser.Parse(file, "---\ntitle: From front\n---\n# Heading\n").Title);
        Assert.Equal("Heading", NoteParser.Parse(file, "intro\n# Heading\n").Title);
        Assert.Equal("My note", NoteParser.Parse(file, "no heading").Title);
    }

    [Fact]
    public void Note_parser_finds_tags_and_links_outside_code_only()
    {
        var note = NoteParser.Parse(new VaultFile("n.md", DateTimeOffset.UnixEpoch),
            "#one text #two/sub https://x.test/#frag `#inline [[Inline]]`\n```\n#fenced [[Fenced]]\n```\n[[Target#Heading|alias]] [[folder/Other]] [[]]\n## Not a tag\n");
        Assert.Equal(new[] { "one", "two/sub" }, note.Tags);
        Assert.Equal(new[] { "Target", "folder/Other" }, note.Links);
    }

    [Fact]
    public void Excerpts_cut_on_whitespace_and_mark_the_ends()
    {
        var (text, truncated) = Excerpt.Head("alpha beta gamma delta epsilon", 12);
        Assert.True(truncated);
        Assert.Equal("alpha beta …", text);
        Assert.Equal(("short", false), Excerpt.Head("  short  ", 12));
        var around = Excerpt.Around("line one\nline   two\nline three", 10, 12);
        Assert.StartsWith("…", around);
        Assert.EndsWith("…", around);
        Assert.DoesNotContain("\n", around);
    }
}
