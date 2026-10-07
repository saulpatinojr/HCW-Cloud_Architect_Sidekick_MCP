# D. Repository structure

```
.
├── AdrMcp.sln
├── Directory.Build.props              # net8.0, nullable, nullable warnings as errors
├── Directory.Packages.props           # central package versions (pinned; verified against nuget.org 2026-10-07)
├── global.json                        # SDK 8.0.4xx, rollForward latestFeature
├── azure.yaml                         # Azure Developer CLI service + infra mapping (postprovision hook)
├── LICENSE · THIRD-PARTY-NOTICES.md · README.md · .gitignore · .editorconfig
├── .github/
│   ├── workflows/{ci-cd,codeql,labeler,stale}.yml   # build/test/scan/deploy, CodeQL, PR labels, stale bot
│   ├── agents/*.agent.md              # GitHub Copilot custom agents (mirror of .claude/agents)
│   ├── mcp/                           # cloud-provider MCP server configs (Azure, AWS, GCP, Terraform, GitHub) + README
│   ├── ISSUE_TEMPLATE/ · PULL_REQUEST_TEMPLATE.md · CODEOWNERS · SECURITY.md · copilot-instructions.md · labeler.yml · dependabot.yml
├── .claude/agents/*.md                # Claude Code agent pack (see .claude/agents/README.md) · .claude/settings.json (permission allow/deny lists)
├── .mcp.json · .vscode/mcp.json       # MCP servers: this ADR server + Azure/AWS/GCP/Terraform/GitHub (no credentials; env/CLI identity)
├── CLAUDE.md · CONTRIBUTING.md · CODE_OF_CONDUCT.md · SUPPORT.md · CHANGELOG.md
├── docs/
│   ├── 00-source-assessment.md        # A
│   ├── 01-architecture.md             # B (Mermaid diagrams, trust boundaries, data flow, failure paths)
│   ├── adr/ADR-0001 … ADR-0006        # C
│   ├── 02-repository-structure.md     # D (this file)
│   ├── 03-mcp-contracts.md            # F (generated from ToolCatalog by tools/AdrMcp.DocGen)
│   ├── 04-security-threat-model.md    # §17 threat model, §9 content safety, §8 upload policy
│   ├── 05-local-dev-and-deploy.md     # H
│   ├── 06-claude-connection.md        # I
│   ├── 07-tests-and-ci.md             # J + K
│   ├── 08-roadmap.md                  # L
│   └── 09-fitness-functions-and-memory.md  # §14, §15, §16
├── infra/
│   ├── main.bicep · main.parameters.json · abbreviations.json
│   └── modules/{identity,monitoring,network,storage,cosmos,keyvault,functionapp,rbac,private-endpoint}.bicep
├── samples/
│   ├── adrs/*.md                      # sample ADRs with testable claims
│   ├── evidence/*                     # csproj, appsettings, Bicep, Dockerfile, package.json samples (drift demo)
│   ├── mcp-requests/*.json            # JSON-RPC bodies for curl / MCP Inspector
│   └── claude/.mcp.json.example       # Claude Code project config
├── scripts/
│   ├── smoke-test.py                  # MCP protocol smoke test (stdlib only)
│   ├── local-mcp-call.sh              # curl helper for one tool call
│   ├── postprovision.sh               # azd hook: Key Vault salt secret
│   └── package.sh                     # zips the deliverable
├── tools/AdrMcp.DocGen/               # generates docs/03-mcp-contracts.md from code
├── src/
│   ├── AdrMcp.Domain/                 # entities, value objects, lifecycle, errors (no dependencies)
│   │   ├── Adrs/ (Adr, HumanOwnedContent, ObservedState, AdrStatus + AdrLifecycle, DecisionClaim, DetectedTechnology, LinkedFile)
│   │   ├── Findings/ (Finding, ContradictionFinding, DriftFinding, SafetyFinding, GovernanceFinding)
│   │   └── Proposals/ · History/ · Audit/ · Jobs/ · Memory/ · Files/ · Tenancy/ · Rules/ · Common/ · Errors/
│   ├── AdrMcp.Application/            # use cases, abstractions, MCP contracts (no Azure SDKs)
│   │   ├── Abstractions/ (repositories, storage, safety, analysis plugins, tenancy)
│   │   ├── Common/ (AppJson, Hashing, Guard, SensitiveDataRedactor, KeywordExtractor, AdrMarkdown, ToolEnvelope)
│   │   ├── Options/ (Adr, Upload, Job, Memory, Audit, Idempotency, Auth options)
│   │   ├── Services/ (AdrService, ChangeProposalService, FileUploadService, EvidenceService, TechnologyService,
│   │   │              DriftService, ContradictionService, SafetyReviewService, AnalysisJobService, AuditService, DecisionMemoryService)
│   │   └── Tools/ (ToolCatalog + JSON schemas, Contracts DTOs, ToolInvocationPipeline, ToolHandlers)
│   ├── AdrMcp.Infrastructure/         # Azure adapters + in-memory doubles
│   │   ├── Cosmos/ (CosmosContext, repositories) · Blob/ (BlobEvidenceStore, BlobEvidenceSetProvider)
│   │   ├── Queue/ (StorageQueueJobQueue) · InMemory/ (all stores) · Tenancy/ (HeaderTenantResolver, RoleAuthorizationService)
│   │   └── ServiceCollectionExtensions.cs (AddAdrApplication, AddAdrPersistence)
│   ├── AdrMcp.Analysis/               # deterministic analyzer plugins
│   │   ├── Safety/ (patterns, CompositeContentSafetyService) · Technology/ (catalog + 8 detectors)
│   │   ├── Claims/ (RegexClaimExtractor) · Drift/ (3 detectors) · Contradiction/ (7 rules) · SafetyReview/ (12 rules)
│   │   └── ServiceCollectionExtensions.cs (AddAdrAnalysis)
│   └── AdrMcp.Functions/              # host: MCP triggers, queue worker, timer, health
│       ├── Program.cs · host.json · local.settings.json.example
│       ├── Mcp/McpToolAdapter.cs · Tools/{AdrTools,FileTools,AnalysisTools,JobAndMemoryTools}.cs
│       └── Jobs/{AnalysisJobWorker,JobHandlers}.cs · Timers/MaintenanceTimer.cs · Http/HealthFunction.cs
└── tests/
    ├── AdrMcp.UnitTests/              # 124 tests: domain, services, analyzers, contracts, security (in-memory persistence)
    └── AdrMcp.IntegrationTests/       # Azurite-backed blob/SAS tests; Cosmos tests gated by env vars
```

Dependency direction: `Functions → Infrastructure, Analysis → Application → Domain`. Application has no Azure SDK references, so every use case is unit-testable with the in-memory stores, and the Functions project is the only place that knows about `ToolInvocationContext`.
