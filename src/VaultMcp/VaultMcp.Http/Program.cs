using VaultMcp.Core;
using VaultMcp.Http;
using VaultMcp.Tools;

var builder = WebApplication.CreateBuilder(args);

VaultHostOptions options;
try
{
    options = VaultHostOptions.From(builder.Configuration, Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

IVaultStore store;
try
{
    store = new LocalFolderVaultStore(options.Path);
}
catch (DirectoryNotFoundException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(sp => new VaultIndex(sp.GetRequiredService<IVaultStore>()));
builder.Services.AddSingleton<VaultService>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(VaultTools).Assembly);

var app = builder.Build();

if (options.Mode == VaultAccessMode.Anonymous)
{
    app.Logger.LogWarning("Vault:Auth:Mode is Anonymous: anyone who can reach this address can read the allow-listed folders. Use only on a machine you control.");
}

// Liveness for the platform. Says whether the settings note exists, never where the vault is.
app.MapGet("/health", async (IVaultStore vault, VaultHostOptions o, CancellationToken ct) =>
{
    var settingsNote = await vault.ReadAsync(VaultConfig.FileName, ct) is not null;
    return Results.Json(new { status = "ok", mode = o.Mode.ToString(), settingsNote });
});

// Fail closed: in Locked mode the MCP endpoint answers 401 to everything until a sign-in layer is configured.
app.Use(async (context, next) =>
{
    if (options.Mode == VaultAccessMode.Locked && context.Request.Path.StartsWithSegments("/mcp"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync("Locked: this vault server has no sign-in configured yet.");
        return;
    }
    await next(context);
});

app.MapMcp("/mcp");

app.Run();
return 0;

/// <summary>Lets the tests host the real pipeline with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
