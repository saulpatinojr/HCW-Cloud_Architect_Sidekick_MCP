using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VaultMcp.Core;

var builder = Host.CreateApplicationBuilder(args);

// `--vault <path>` on the command line, or the VAULT_PATH environment variable. Nothing else is configurable here:
// the allow-list and the rest live in adr-sidekick.md inside the vault, so they follow it through Obsidian Sync.
var vaultPath = builder.Configuration["vault"] ?? Environment.GetEnvironmentVariable("VAULT_PATH");
if (string.IsNullOrWhiteSpace(vaultPath))
{
    Console.Error.WriteLine("Usage: VaultMcp.Stdio --vault <path to the Obsidian vault folder>   (or set VAULT_PATH)");
    return 2;
}

// stdout carries the protocol, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IVaultStore>(new LocalFolderVaultStore(vaultPath));
builder.Services.AddSingleton(sp => new VaultIndex(sp.GetRequiredService<IVaultStore>()));
builder.Services.AddSingleton<VaultService>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
