namespace VaultMcp.Core;

/// <summary>Reads the vault straight from its folder on disk. Obsidian does not need to be running. Never writes.</summary>
public sealed class LocalFolderVaultStore : IVaultStore
{
    private readonly string _root;

    public LocalFolderVaultStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The vault folder path is required.", nameof(root));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"Vault folder not found: {_root}");
    }

    public Task<IReadOnlyList<VaultFile>> ListAsync(CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Symbolic links are skipped so a link inside the vault cannot point the reader at a folder outside it.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var list = new List<VaultFile>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.md", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = VaultPath.Normalize(Path.GetRelativePath(_root, file));
            if (relative is null || VaultPath.IsHidden(relative)) continue;
            list.Add(new VaultFile(relative, File.GetLastWriteTimeUtc(file)));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return Task.FromResult<IReadOnlyList<VaultFile>>(list);
    }

    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (full is null || !File.Exists(full)) return null;
        return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
    }

    /// <summary>Maps a relative vault path to a full path inside the root, or null when it would land outside the root.</summary>
    internal string? Resolve(string? relative)
    {
        var normalized = VaultPath.Normalize(relative);
        if (normalized is null) return null;
        var full = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
