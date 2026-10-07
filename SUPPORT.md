# Support

- **Setup and usage questions**: open a GitHub Discussion or an issue with the "Setup" label. Start with `README.md`, `docs/05-local-dev-and-deploy.md` and `docs/06-claude-connection.md`.
- **Bugs**: use the bug report template. Include the `correlationId` from the tool response envelope; never include keys, SAS URLs or connection strings.
- **Security issues**: follow `.github/SECURITY.md` (private reporting only).
- **Operational runbooks**: `docs/05-local-dev-and-deploy.md` (logs, clean-up), `docs/04-security-threat-model.md` (retention, key rotation), `docs/09-fitness-functions-and-memory.md` (jobs and poison queue).

Self-diagnosis checklist for connection problems:
1. `GET https://<app>.azurewebsites.net/api/health` returns `{"status":"ok",...}`.
2. `python3 scripts/smoke-test.py --url <mcp-endpoint> --key <system-key>` passes `initialize` and `tools/list`.
3. In Claude Code, `claude mcp list` shows the server as Connected; `/mcp` shows "Needs authentication" if the key or token is missing.
4. Check Application Insights traces filtered by `customDimensions.CorrelationId`.
