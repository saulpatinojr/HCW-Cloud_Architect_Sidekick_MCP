#!/usr/bin/env bash
# azd postprovision hook: creates the principal-hash salt secret in Key Vault (never stored in source) if it does not exist.
# Requires the deployer to hold "Key Vault Secrets Officer" on the vault (granted when AZURE_PRINCIPAL_ID is supplied).
set -euo pipefail
VAULT="${AZURE_KEY_VAULT_NAME:?AZURE_KEY_VAULT_NAME output missing}"
if ! az keyvault secret show --vault-name "$VAULT" --name principal-hash-salt --query id -o tsv >/dev/null 2>&1; then
  SALT=$(openssl rand -base64 48)
  az keyvault secret set --vault-name "$VAULT" --name principal-hash-salt --value "$SALT" --query id -o tsv >/dev/null
  echo "Created Key Vault secret principal-hash-salt in $VAULT"
else
  echo "Key Vault secret principal-hash-salt already exists"
fi
echo "MCP endpoint: ${MCP_ENDPOINT:-unknown}"
echo "Retrieve the mcp_extension system key with: az functionapp keys list -g ${AZURE_RESOURCE_GROUP} -n ${AZURE_FUNCTION_APP_NAME} --query systemKeys.mcp_extension -o tsv"
