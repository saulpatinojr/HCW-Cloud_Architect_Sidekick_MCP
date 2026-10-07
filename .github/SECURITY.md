# Security policy

## Supported versions

The `main` branch and the latest tagged release receive security fixes.

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Use GitHub's private vulnerability reporting ("Report a vulnerability" under the Security tab) or contact the repository owners listed in `.github/CODEOWNERS`. Include reproduction steps, impact, and affected components (tool name, file, Bicep module). You will receive an acknowledgement within five business days.

## Scope

- MCP tool authorization, tenant/workspace isolation, idempotency and audit behaviour
- Upload flow (SAS scope, quarantine, hashing, MIME checks, content safety)
- Secret handling, redaction and logging
- Infrastructure templates (RBAC, networking, Key Vault, storage hardening)

## Out of scope

- Findings produced by the advisory analyzers (false positives/negatives are product issues, not vulnerabilities)
- Issues in third-party MCP servers configured under `.github/mcp/`

## Hardening baseline

See `docs/04-security-threat-model.md`. Never commit `local.settings.json`, `.azure/`, keys, SAS tokens or connection strings; CI runs gitleaks and `dotnet list package --vulnerable` on every pull request.
