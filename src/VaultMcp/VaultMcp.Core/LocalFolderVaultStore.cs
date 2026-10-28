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

    /// <summary>
    /// The file's text, or null when the path leaves the root, any part of it is a symbolic link, the file is missing,
    /// or it cannot be read right now (for example while a sync client holds it). A single unreadable note must not
    /// take the whole index down, so those cases are skipped rather than thrown; cancellation still propagates.
    /// </summary>
    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (full is null || !IsRegularFileWithoutLinks(full)) return null;
        try
        {
            return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Maps a relative vault path to a full path inside the root, or null when it would land outside the root.</summary>
    internal string? Resolve(string? relative)
    {
        var normalized = VaultPath.Normalize(relative);
        if (normalized is null) return null;
        var full = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// True only for an existing regular file whose every path component below the root is a real directory or file,
    /// not a symbolic link or other reparse point. <see cref="Path.GetFullPath(string)"/> is lexical, so a link inside
    /// the vault could otherwise point a read at a folder outside it.
    /// </summary>
    internal bool IsRegularFileWithoutLinks(string full)
    {
        var relative = Path.GetRelativePath(_root, full);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        }
        return File.Exists(current);
    }
}
