namespace VaultMcp.Core;

/// <summary>A Markdown file in the vault: its path relative to the vault root with '/' separators, and its last write time.</summary>
public sealed record VaultFile(string Path, DateTimeOffset ModifiedUtc);

/// <summary>A parsed note. <see cref="Body"/> excludes the front matter block.</summary>
public sealed record VaultNote(string Path, string Title, IReadOnlyList<string> Tags, IReadOnlyList<string> Links, DateTimeOffset ModifiedUtc, string Body)
{
    /// <summary>File name without the .md extension: what a <c>[[wiki link]]</c> names.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>Where the notes come from. Phase 1 is a local folder; phase 2 (ADR-0010) binds the same core to a Blob container.</summary>
public interface IVaultStore
{
    /// <summary>Every Markdown file in the vault except hidden folders (.obsidian, .trash, any dot-folder). Paths are relative with '/' separators, sorted.</summary>
    Task<IReadOnlyList<VaultFile>> ListAsync(CancellationToken ct);

    /// <summary>The file's text, or null when it does not exist or the path would leave the vault.</summary>
    Task<string?> ReadAsync(string path, CancellationToken ct);
}

/// <summary>A refusal the tools report as <c>{ "error": { code, message } }</c>. Messages never name a file outside the allow-list.</summary>
public sealed class VaultException : Exception
{
    public string Code { get; }
    public VaultException(string code, string message) : base(message) => Code = code;

    public static VaultException NotFound() => new("NOT_FOUND", "Note not found, or not inside an allow-listed folder.");
}
