namespace VaultMcp.Core;

/// <summary>Relative vault paths: always '/' separated, never rooted, never containing '.' or '..' segments.</summary>
public static class VaultPath
{
    /// <summary>Returns the clean relative form, or null when the path is empty, rooted, drive-qualified or tries to climb out.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || p.Contains(':')) return null;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        foreach (var s in segments)
        {
            if (s is "." or "..") return null;
        }
        return string.Join('/', segments);
    }

    /// <summary>True when any segment starts with a dot (.obsidian, .trash, .git).</summary>
    public static bool IsHidden(string normalized)
    {
        foreach (var s in normalized.Split('/'))
        {
            if (s.StartsWith('.')) return true;
        }
        return false;
    }
}
