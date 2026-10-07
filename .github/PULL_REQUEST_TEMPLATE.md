## Summary

<!-- What changes and why. Link the ADR or issue. -->

## Type of change

- [ ] Bug fix
- [ ] New tool or analyzer (ToolCatalog / detector / rule)
- [ ] Infrastructure (Bicep, azd, workflows)
- [ ] Documentation
- [ ] Security hardening

## Checklist

- [ ] `dotnet build AdrMcp.sln -c Release` has no warnings
- [ ] `dotnet test AdrMcp.sln` is green (new behaviour has tests)
- [ ] Tool changes: `ToolCatalog`, DTOs, handlers, `[McpToolTrigger]`, `TestHost.Dispatch` and `docs/03-mcp-contracts.md` (regenerated) are in sync
- [ ] Infra changes: `bicep build` and `bicep lint` clean; what-if reviewed
- [ ] No secrets, keys, SAS tokens, connection strings or real PII anywhere in the diff (placeholders only)
- [ ] Human-owned ADR fields still have no automated write path (`HumanOwnedFieldProtectionTests` pass)
- [ ] Tenant/workspace still resolved from the principal, never from arguments
- [ ] Docs/README/CHANGELOG updated where behaviour changed

## Security review

- [ ] Not needed (docs/tests only)
- [ ] Requested from CODEOWNERS (touches Infrastructure, Safety, Tools or infra/)

## Screenshots / smoke test output

<!-- Paste the `scripts/smoke-test.py` summary lines if relevant. Never paste keys or URLs containing `sig=`. -->
