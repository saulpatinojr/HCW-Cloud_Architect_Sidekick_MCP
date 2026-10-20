# ADR-0007: Move the runtime from .NET 8 to .NET 10 (LTS) before .NET 8 end of support

- Status: Accepted
- Date: 2026-10-16
- Owners: Platform architecture
- Tags: runtime, dotnet, azure-functions, lifecycle

## Context
The server runs on the .NET 8 isolated worker on Flex Consumption (ADR-0001 names the ".NET 8 isolated worker" in its Decision). Repository notes (`docs/00-source-assessment.md`) record that .NET 8 LTS, and with it .NET 8 isolated support in Azure Functions, ends on **2026-11-10**, and that the official `Azure-Samples/remote-mcp-functions-dotnet` sample targets net10.0 on Flex Consumption. **Unverified**: Microsoft documentation was unreachable when this ADR was drafted, so neither fact was re-checked.

Before this change (**Observed**): `net8.0` is set once, in `Directory.Build.props` (`LangVersion` 12.0); `global.json` pins SDK `8.0.400` with `rollForward: latestFeature`; `infra/modules/functionapp.bicep` hard-codes `runtime: { name: 'dotnet-isolated', version: '8.0' }`; `Microsoft.Extensions.*` are already 10.0.x; `Microsoft.Azure.Functions.Worker` 2.52.0, `Worker.Sdk` 2.1.0, `Worker.Extensions.Mcp` 1.6.0.

Spike on SDK 10.0.112, 2026-10-16 (**Observed**): with `TargetFramework` `net10.0`, `global.json` pointed at a 10.0 SDK, no other code change and `LangVersion` 12 kept, the solution builds with 0 warnings and 0 errors; 124/124 unit tests and 6/6 Azurite integration tests pass; the regenerated `docs/03-mcp-contracts.md` is byte-identical; `dotnet publish` emits `functions.metadata` and `worker.config.json`.

**Not verified**: (a) that Flex Consumption accepts `dotnet-isolated` `10.0` in the target regions; (b) Functions host and MCP extension behaviour on .NET 10 at runtime (Core Tools could not be installed for the spike).

Forces: no security patches after end of support for a process that parses untrusted evidence; a short window; change one variable at a time; keep a rollback path while .NET 8 is still supported.

## Decision
Retarget to .NET 10 (LTS) without changing the language version or any package version in the same move:
1. `Directory.Build.props`: `TargetFramework` `net10.0`; `LangVersion` stays `12.0`.
2. `global.json`: SDK `10.0.100`, `rollForward: latestFeature` (same policy as before).
3. Bicep: the Flex runtime version is a parameter, `functionsDotnetVersion` in `infra/main.bicep` (`@allowed([ '8.0', '10.0' ])`, default `'10.0'`), passed to `functionapp.bicep`.

Gates before this ADR can be Accepted:
- `az functionapp list-flexconsumption-runtimes --location <region> --runtime dotnet-isolated` lists `10.0` for every target region.
- On .NET 10, `func start` followed by `python3 scripts/smoke-test.py --url http://localhost:7071/runtime/webhooks/mcp --write` passes.
- CI is green (build, tests, NuGet Audit, Bicep lint, contract docs).

## Gate evidence (2026-10-20)
- Flex runtimes (**Observed**): `az functionapp list-flexconsumption-runtimes --location southcentralus --runtime dotnet-isolated` lists `10` (end of life 2028-11-10), `9` and `8` (both 2026-11-10). `southcentralus` is the only target region (adr-dev).
- Local runtime (**Observed**): on .NET 10.0.12 (SDK 10.0.402, Core Tools 4.15.2, Azurite 3.37.0), `func start` followed by `python scripts/smoke-test.py --url http://localhost:7071/runtime/webhooks/mcp --write` passed: initialize, `tools/list` (27 tools), `adr_list`, `technology_detect`, `adr_safety_review` and the write round-trip.
- CI (**Observed**): green on `main` at `94a1efb` (run 39026145689): build, unit and Azurite integration tests, NuGet Audit, Bicep build and lint, contract docs.
- Azure (**Observed**): the same run deployed `94a1efb` to adr-dev on Flex Consumption with `functionsDotnetVersion` `10.0`; the health probe and the smoke test passed. This also settles "Not verified (b)" above for the deployed host.
- Dates (**Verified**, Microsoft Learn, [Azure Functions runtime versions](https://learn.microsoft.com/azure/azure-functions/functions-versions#languages)): .NET 8 support on Functions v4 ends 2026-11-10; .NET 10 is GA with expected end of support 2028-11-14.

## Rationale
- .NET 10 is the LTS release that follows .NET 8; a runtime past end of support receives no security fixes.
- The spike shows build, tests and generated contracts are unaffected; no source, analyzer or contract edits were needed.
- `Microsoft.Extensions.*` 10.0.x are already in use, so the runtime catches up with the libraries.
- Keeping C# 12 means any behaviour difference after the move comes from the runtime alone.
- A Bicep parameter turns a runtime rollback into a deployment value instead of a template edit.

## Considered alternatives
1. Stay on .NET 8 past 2026-11-10 — rejected: unpatched runtime.
2. .NET 9 (STS) — rejected: shorter support than .NET 10, so another migration would follow soon.
3. .NET 10 and C# 14 together — rejected: two changes at once are harder to attribute and roll back; the language upgrade can follow on its own.

## Consequences
- Contributors need a .NET 10 SDK (10.0.1xx or a later 10.0 band) and a Core Tools version that supports .NET 10 isolated.
- CI needs no workflow change: `actions/setup-dotnet` reads `global.json`.
- Sample evidence and test fixtures that contain `net8.0` are data and stay unchanged.
- This ADR replaces only the runtime-version clause of ADR-0001; the MCP-extension decision stands. Under ADR-0005 the approvers decide how to record that link.
- Deployment order (**Inferred**): for an already-deployed app, `azd provision` switches the runtime before `azd deploy` ships the net10.0 package, so expect a short gap. The first deployment is unaffected.
- If the gates are not met before 2026-11-10, running .NET 8 past end of support needs an explicit, dated risk acceptance from the owners.

## Rollback
- Runtime: deploy with `functionsDotnetVersion = '8.0'` together with a net8.0 build.
- Code: revert `TargetFramework` and `global.json`, then `azd up`.
- .NET 8 is a supported rollback target only until 2026-11-10 (**Unverified** date).
