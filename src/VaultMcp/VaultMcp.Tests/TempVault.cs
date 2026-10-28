using VaultMcp.Core;

namespace VaultMcp.Tests;

/// <summary>A throw-away vault on disk. Allowed: Work, Projects/Cloud. Not allowed: Personal, .obsidian, anything else.</summary>
public sealed class TempVault : IDisposable
{
    public string Root { get; }

    public TempVault(bool withConfig = true)
    {
        Root = Directory.CreateTempSubdirectory("vaultmcp-").FullName;
        if (withConfig)
        {
            Write(VaultConfig.FileName, "---\nallow:\n  - Work\n  - Projects/Cloud\ntag: adr-context\nexcerpt_chars: 300\nmax_results: 5\n---\nSettings for the ADR sidekick.\n");
        }
        Write("Work/Cosmos decision.md", "# Cosmos DB over Table Storage\n#adr-context #azure/cosmos\n\nWe chose Cosmos because the query model fits. See [[Naming convention]] and [[Personal/Diary]].\n\n```\n#notatag [[Not a link]]\n```\n");
        Write("Work/Naming convention.md", "---\ntitle: Azure naming convention\ntags: [adr-context, azure]\n---\nResource names follow abbreviation, workload, environment, region, instance. Cosmos accounts use cosno.\n");
        Write("Projects/Cloud/Landing zone.md", "Hub and spoke. Data lives in [[Cosmos decision|the cosmos note]] and follows [[Work/Naming convention#Pattern]].\n");
        Write("Personal/Diary.md", "# Diary\nSecret personal note about cosmos and naming. #adr-context\n");
        Write(".obsidian/plugins/x/notes.md", "cosmos naming adr-context hidden plugin file");
        Write("Work/.trash/Old.md", "cosmos in a hidden folder");
    }

    public void Write(string relative, string text)
    {
        var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public VaultService Service(TimeSpan? ttl = null) => new(new VaultIndex(new LocalFolderVaultStore(Root), ttl: ttl ?? TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
