# Obsidian Headless sync sidecar for the vault MCP server (ADR-0010 phase 2, step 2).
# Build:  docker build -t vault-sync src/VaultMcp/sync
# Run:    docker run --rm -v "<copy>:/vault" -v "<state>:/state" -e OB_VAULT="My Vault" -e SYNC_EXCLUDE_SEED=Personal vault-sync
# In Azure the two mounts are the file shares of the vault storage account and the settings come from the Container App.
FROM node:22-bookworm-slim
# The headless client is an open beta; the version is pinned so a deploy never picks up a surprise.
RUN npm install -g obsidian-headless@0.0.14 && npm cache clean --force \
    && groupadd --gid 1001 sync && useradd --uid 1001 --gid 1001 --create-home --shell /usr/sbin/nologin sync \
    && mkdir -p /vault /state && chown sync:sync /vault /state
WORKDIR /app
COPY --chown=sync:sync package.json sync.mjs exclusions.mjs ./
USER sync
# `ob` keeps its login under the home directory; the state share is mounted there so a restart does not log out.
ENV HOME=/state \
    VAULT_PATH=/vault \
    STATE_PATH=/state \
    NODE_ENV=production
VOLUME ["/vault", "/state"]
ENTRYPOINT ["node", "/app/sync.mjs"]
