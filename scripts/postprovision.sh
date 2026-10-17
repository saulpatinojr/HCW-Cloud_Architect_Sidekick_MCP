#!/usr/bin/env bash
# azd postprovision hook: creates the principal-hash salt secret in Key Vault (never stored in source) if it does not exist.
# Requires the deployer to hold "Key Vault Secrets Officer" on the vault (granted when AZURE_PRINCIPAL_ID is supplied).
#
# That role assignment is created by the same `azd provision` run, and Azure RBAC can take several minutes to propagate.
# A 403 therefore means "not authorized yet", not "secret missing": retry with backoff, and create the secret only after
# Key Vault has answered SecretNotFound, so an existing salt is never overwritten (that would orphan every stored hash).
set -euo pipefail
VAULT="${AZURE_KEY_VAULT_NAME:?AZURE_KEY_VAULT_NAME output missing}"
SECRET="principal-hash-salt"
MAX_ATTEMPTS="${ADR_KV_MAX_ATTEMPTS:-20}"
DELAY_SECONDS="${ADR_KV_RETRY_DELAY:-15}"

is_forbidden() { case "$1" in *Forbidden*|*"Status code 403"*|*"(403)"*) return 0 ;; *) return 1 ;; esac; }

attempt=1
while :; do
  if out=$(az keyvault secret show --vault-name "$VAULT" --name "$SECRET" --query id -o tsv 2>&1); then
    echo "Key Vault secret $SECRET already exists"
    break
  fi
  if [[ "$out" == *SecretNotFound* ]]; then
    salt=$(openssl rand -base64 48)
    if out=$(az keyvault secret set --vault-name "$VAULT" --name "$SECRET" --value "$salt" --query id -o tsv 2>&1); then
      echo "Created Key Vault secret $SECRET in $VAULT"
      break
    fi
  fi
  if ! is_forbidden "$out"; then
    echo "$out" >&2
    echo "Unexpected Key Vault error for $SECRET in $VAULT (see above); not retrying." >&2
    exit 1
  fi
  if [ "$attempt" -ge "$MAX_ATTEMPTS" ]; then
    echo "Still not authorized on $VAULT after $attempt attempts. Check the Key Vault Secrets Officer assignment for the deploying principal, then re-run 'azd provision'." >&2
    exit 1
  fi
  echo "Waiting for the Key Vault role assignment to propagate (attempt $attempt/$MAX_ATTEMPTS, retrying in ${DELAY_SECONDS}s)..."
  sleep "$DELAY_SECONDS"
  attempt=$((attempt + 1))
done

echo "MCP endpoint: ${MCP_ENDPOINT:-unknown}"
echo "Retrieve the mcp_extension system key with: az functionapp keys list -g ${AZURE_RESOURCE_GROUP:-<rg>} -n ${AZURE_FUNCTION_APP_NAME:-<app>} --query systemKeys.mcp_extension -o tsv"
