# Contributing

Thank you for helping improve the ADR Intelligence MCP Server.

## Before you start
- Read `README.md`, `CLAUDE.md` and `docs/04-security-threat-model.md`.
- Open an issue (bug, feature or architecture decision proposal) before large changes so the design can be discussed.
- Architecture changes to this project are recorded as ADRs in `docs/adr/`.

## Development setup
```bash
git clone https://github.com/saulpatinojr/HCW-Cloud_Architect_Sidekick_MCP.git
cd HCW-Cloud_Architect_Sidekick_MCP
npm i -g azure-functions-core-tools@4 azurite
cp src/AdrMcp.Functions/local.settings.json.example src/AdrMcp.Functions/local.settings.json
dotnet build AdrMcp.sln
dotnet test AdrMcp.sln
```
Run the server locally with Azurite and `func start` as described in `docs/05-local-dev-and-deploy.md`.

## Pull requests
1. Branch from `main` (`feature/<topic>` or `fix/<topic>`).
2. Keep PRs focused; include tests for behaviour changes.
3. Run `dotnet build -c Release` (no warnings), `dotnet test`, and `bicep build`/`bicep lint` for infra changes.
4. If you touched tools, regenerate `docs/03-mcp-contracts.md` (`dotnet run --project tools/AdrMcp.DocGen -- docs/03-mcp-contracts.md`).
5. Fill in the PR template; changes under `src/AdrMcp.Infrastructure`, `src/AdrMcp.Analysis/Safety`, `src/AdrMcp.Application/Tools` or `infra/` need a security review from CODEOWNERS.
6. Never include secrets, SAS URLs, connection strings or real personal data in code, tests, fixtures or screenshots.

## Adding analyzers
- Technology detector: implement `ITechnologyDetector` (pure function of path + content), register in `AdrMcp.Analysis/ServiceCollectionExtensions.cs`, add tests in `DetectorTests`.
- Drift detector: implement `IDriftDetector` for a `ClaimKind`; return `DriftEvaluation` with evidence items and confidence; add compliant/drifted/uncertain tests.
- Contradiction rule: implement `IContradictionRule`; respect `ClaimScope.Overlaps`; add a true-positive and a false-positive scenario test.
- Safety-review rule: extend `SafetyRuleBase`; fill observed evidence, inference, recommendation and mitigations; never make the rule blocking in code (policy does that).

## Commit style
Imperative, scoped messages: `analysis: add helm values detector`, `infra: enable private DNS zones for queue endpoint`.

## Code of conduct
This project follows `CODE_OF_CONDUCT.md`.
