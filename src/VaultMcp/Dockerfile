# Obsidian vault MCP server, HTTP host (ADR-0010 phase 2, Route A).
# Build from the repository root, because the central package and build settings live there:
#   docker build -f src/VaultMcp/Dockerfile -t vault-mcp .
# Run with the vault copy mounted at /vault (read-only is enough; the server never writes):
#   docker run --rm -p 8080:8080 -v "<vault>:/vault:ro" -e Vault__Auth__Mode=Anonymous vault-mcp
# Without Vault__Auth__Mode the server starts Locked and answers 401 on /mcp until a sign-in layer is configured.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/VaultMcp/VaultMcp.Core/VaultMcp.Core.csproj src/VaultMcp/VaultMcp.Core/
COPY src/VaultMcp/VaultMcp.Tools/VaultMcp.Tools.csproj src/VaultMcp/VaultMcp.Tools/
COPY src/VaultMcp/VaultMcp.Http/VaultMcp.Http.csproj src/VaultMcp/VaultMcp.Http/
RUN dotnet restore src/VaultMcp/VaultMcp.Http/VaultMcp.Http.csproj
COPY src/VaultMcp/VaultMcp.Core/ src/VaultMcp/VaultMcp.Core/
COPY src/VaultMcp/VaultMcp.Tools/ src/VaultMcp/VaultMcp.Tools/
COPY src/VaultMcp/VaultMcp.Http/ src/VaultMcp/VaultMcp.Http/
RUN dotnet publish src/VaultMcp/VaultMcp.Http/VaultMcp.Http.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
# The non-root user the aspnet image ships; Kestrel listens on 8080 for it.
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Vault__Path=/vault \
    DOTNET_EnableDiagnostics=0
VOLUME ["/vault"]
EXPOSE 8080
# No Docker HEALTHCHECK on purpose: the runtime image has neither an SDK nor curl, and Azure Container Apps ignores
# the instruction. Liveness and readiness probes against GET /health are declared in the Container Apps Bicep.
ENTRYPOINT ["dotnet", "VaultMcp.Http.dll"]
