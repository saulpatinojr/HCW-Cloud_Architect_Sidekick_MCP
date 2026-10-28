namespace VaultMcp.Http;

/// <summary>Who may call the MCP endpoint. Settings use <c>Vault__Auth__Mode</c> (the <c>__</c> stands for <c>:</c>).</summary>
public enum VaultAccessMode
{
    /// <summary>Default. Every call to the MCP endpoint is refused with 401 until a sign-in layer is configured (ADR-0010 phase 2 step 3). Fails closed.</summary>
    Locked,

    /// <summary>No sign-in: anyone who can reach the address can read the allow-listed folders. For a machine you control only; refused inside Azure Container Apps.</summary>
    Anonymous,
}

/// <summary>Host settings. The allow-list and everything about the notes stay in <c>adr-sidekick.md</c> inside the vault.</summary>
public sealed record VaultHostOptions(string Path, VaultAccessMode Mode)
{
    public const string PathKey = "Vault:Path";
    public const string ModeKey = "Vault:Auth:Mode";

    /// <summary>Set by Azure Container Apps on every replica. Anonymous mode is refused when it is present.</summary>
    public const string ContainerAppsMarker = "CONTAINER_APP_NAME";

    /// <summary>Reads and checks the settings; a bad value stops the host with a plain message instead of a weaker default.</summary>
    public static VaultHostOptions From(IConfiguration configuration, Func<string, string?> environment)
    {
        var path = configuration[PathKey] ?? environment("VAULT_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Set {PathKey} (or VAULT_PATH) to the folder that holds the vault copy.");
        }

        var modeText = configuration[ModeKey];
        var mode = VaultAccessMode.Locked;
        if (!string.IsNullOrWhiteSpace(modeText) && !Enum.TryParse(modeText, ignoreCase: true, out mode))
        {
            throw new InvalidOperationException($"{ModeKey} must be Locked or Anonymous; '{modeText}' is neither.");
        }

        if (mode == VaultAccessMode.Anonymous && !string.IsNullOrEmpty(environment(ContainerAppsMarker)))
        {
            throw new InvalidOperationException($"{ModeKey}=Anonymous is refused inside Azure Container Apps ({ContainerAppsMarker} is set): the endpoint would be open to anyone who can reach it.");
        }

        return new VaultHostOptions(path, mode);
    }
}
