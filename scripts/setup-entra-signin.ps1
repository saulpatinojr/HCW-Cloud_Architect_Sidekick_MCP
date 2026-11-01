<#
.SYNOPSIS
  Re-runnable setup of the Entra objects for MCP sign-in (docs/11-entra-sign-in.md), with a read-back PASS check.
.DESCRIPTION
  Creates or updates, in the tenant you are signed in to, exactly what the runbook in docs/11 creates by hand:
    - the ADR server app registration: single tenant, v2 tokens, the app roles and the delegated scope from scripts/entra/,
      api://<client id> as Application ID URI (plus the MCP URL when -ResourceUri is given), a service principal that
      requires assignment;
    - with -Vault, the same for the Obsidian vault server (ADR-0010 phase 2 step 3) from scripts/entra/vault-*.json;
    - one public client app for Claude Code and the hosted Claude apps (loopback callback on -CallbackPort, and
      https://claude.ai/api/mcp/auth_callback), pre-authorized on each server's scope;
    - optionally (-ConfidentialClient) a confidential client app for Antigravity and Perplexity; its secret is NOT created
      here (the command is printed; the secret belongs in Key Vault or the connector dialog, never in a file or a chat);
    - role assignments: you (or -AdminObjectId) as Adr.Admin, and Vault.Reader on the vault server; with -PipelineClientId
      the deploy pipeline's service principal as Adr.Reader.Application (read-only by rule in the server).
  It ends by reading everything back from Entra and prints PASS per item, or stops with what failed.
  Safe to re-run: existing apps are found by display name and updated, never duplicated; assignments that exist are kept.
  Nothing secret is created, printed or written. Report step numbers and PASS/FAIL lines, never the ids it prints.

  Needs the Azure CLI (az). Works in Windows PowerShell 5.1 and PowerShell 7. You need permission to create app
  registrations and to assign app roles (Application Administrator or Cloud Application Administrator; the assignment call
  needs AppRoleAssignment.ReadWrite.All, see Microsoft Learn "Grant an appRoleAssignment for a service principal").
  Run it from the repository root, or anywhere: it finds scripts/entra/ next to itself.
.EXAMPLE
  .\scripts\setup-entra-signin.ps1
.EXAMPLE
  .\scripts\setup-entra-signin.ps1 -Vault -ResourceUri https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp -VaultResourceUri https://ca-obsidian-dev-scus-01.example.southcentralus.azurecontainerapps.io/mcp
.EXAMPLE
  .\scripts\setup-entra-signin.ps1 -PipelineClientId <AZURE_CLIENT_ID> -PreauthorizeAzureCli
#>
[CmdletBinding()]
param(
    [string]$TenantId = '',                                   # empty: the tenant az is signed in to
    [string]$ServerAppName = 'ADR MCP server',
    [string]$ResourceUri = '',                                # the ADR server's MCP URL, added as an Application ID URI (clients send it as the OAuth resource)
    [switch]$Vault,                                           # also set up the Obsidian vault server's app (ADR-0010)
    [string]$VaultAppName = 'Obsidian vault MCP server',
    [string]$VaultResourceUri = '',                           # the vault server's https://<fqdn>/mcp
    [string]$PublicClientName = 'MCP clients (public)',
    [int]$CallbackPort = 8976,                                # Claude Code: claude mcp add ... --callback-port
    [switch]$ConfidentialClient,
    [string]$ConfidentialClientName = 'MCP clients (confidential)',
    [string]$AdminObjectId = '',                              # empty: the signed-in user
    [string]$PipelineClientId = '',                           # AZURE_CLIENT_ID of the deploy job: gets Adr.Reader.Application
    [switch]$PreauthorizeAzureCli                             # sandbox or owner-only: lets `az account get-access-token` drive the smoke test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Graph = 'https://graph.microsoft.com/v1.0'
$AzureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'      # Microsoft's public client id for the Azure CLI
$ClaudeCallback = 'https://claude.ai/api/mcp/auth_callback'   # the hosted Claude apps (claude.ai, Desktop, mobile, Cowork)
$Definitions = Join-Path $PSScriptRoot 'entra'
$Failures = New-Object System.Collections.Generic.List[string]
$Notes = New-Object System.Collections.Generic.List[string]

# Native commands don't honour $ErrorActionPreference, so every call is checked explicitly.
function Assert-Ok([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

# stdout of a native command that may fail (for example a 404), or '' when it does; stderr is discarded.
function Get-CliOutput([scriptblock]$Command) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = & $Command 2> $null; if ($LASTEXITCODE -eq 0) { return ("$out").Trim() } return '' } finally { $ErrorActionPreference = $previous }
}

# True when the native command exits 0; stderr is discarded.
function Test-Cli([scriptblock]$Command) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Command *> $null; return ($LASTEXITCODE -eq 0) } finally { $ErrorActionPreference = $previous }
}

# JSON from a CLI. foreach flattens arrays the same way in Windows PowerShell 5.1 and 7.
function ConvertFrom-CliJson([string[]]$Lines) {
    foreach ($item in (($Lines | Out-String) | ConvertFrom-Json)) { $item }
}

function Get-Field($Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name }
}

# JSON bodies go to az through files (@file): no quoting differences between PowerShell versions and az.cmd. No secrets are written.
$Work = Join-Path ([IO.Path]::GetTempPath()) ("entra-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
function Write-WorkFile([string]$Name, [string]$Content) {
    $path = Join-Path $Work $Name
    [IO.File]::WriteAllText($path, $Content)
    return $path
}

function Read-Definition([string]$File) {
    $path = Join-Path $Definitions $File
    if (-not (Test-Path -LiteralPath $path)) { throw "$path is missing; run the script from a clone of the repository." }
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}

function Test-Guid([string]$Value) { [Guid]::TryParse($Value, [ref]([Guid]::Empty)) }

# Finds the one app registration with this display name, or $null. Several is an error: the script never picks one.
function Find-App([string]$DisplayName) {
    $json = az ad app list --filter "displayName eq '$DisplayName'" -o json; Assert-Ok 'az ad app list'
    $apps = @(ConvertFrom-CliJson $json)
    if ($apps.Count -gt 1) { throw "Several app registrations are named '$DisplayName'. Rename all but one, or pass another name." }
    if ($apps.Count -eq 1) { return $apps[0] }
    return $null
}

function Get-ServicePrincipalId([string]$AppId) {
    $id = Get-CliOutput { az ad sp list --filter "appId eq '$AppId'" --query '[0].id' -o tsv }
    if ($id) { return $id }
    $id = az ad sp create --id $AppId --query id -o tsv; Assert-Ok 'az ad sp create'
    Write-Host '    created service principal'
    return $id
}

# A resource server: roles, scope, identifier URIs, service principal with assignment required. Returns ids and the scope id.
function Set-ServerApp([string]$DisplayName, [string]$RolesFile, [string]$ScopesFile, [string]$McpUrl) {
    Write-Host "== $DisplayName"
    $roles = @(Read-Definition $RolesFile)
    $scopes = @((Read-Definition $ScopesFile).api.oauth2PermissionScopes)
    $rolesPath = Join-Path $Definitions $RolesFile
    $scopesPath = Join-Path $Definitions $ScopesFile

    $app = Find-App $DisplayName
    if ($null -eq $app) {
        $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --requested-access-token-version 2 --app-roles "@$rolesPath" --query appId -o tsv
        Assert-Ok 'az ad app create'
        Write-Host '    created app registration'
    } else {
        $appId = $app.appId
        # Re-applying the file keeps existing roles and adds new ones (Entra refuses to drop an enabled role; the files never drop one).
        az ad app update --id $appId --sign-in-audience AzureADMyOrg --requested-access-token-version 2 --app-roles "@$rolesPath"; Assert-Ok 'az ad app update'
        Write-Host '    app registration exists; roles and token version applied'
    }
    $objectId = az ad app show --id $appId --query id -o tsv; Assert-Ok 'az ad app show'

    # The delegated scope. PATCH sets the collection; the file holds the same id on every run, so this is idempotent.
    az rest --method PATCH --uri "$Graph/applications/$objectId" --headers 'Content-Type=application/json' --body "@$scopesPath"; Assert-Ok 'az rest PATCH oauth2PermissionScopes'

    # Identifier URIs: api://<client id> always; the MCP URL when given, because clients send it as the OAuth resource (RFC 8707)
    # and Entra issues a token only for a registered Application ID URI (AADSTS9010010 otherwise).
    $wanted = @("api://$appId")
    if ($McpUrl) { $wanted += $McpUrl.TrimEnd('/') }
    $current = @(ConvertFrom-CliJson (az ad app show --id $appId --query identifierUris -o json)); Assert-Ok 'az ad app show'
    $missing = @($wanted | Where-Object { $current -notcontains $_ })
    if ($missing.Count) {
        $uriArgs = @($current + $missing | Select-Object -Unique)
        if (Test-Cli { az ad app update --id $appId --identifier-uris @uriArgs }) {
            Write-Host "    identifier URIs set ($($uriArgs.Count))"
        } elseif ($McpUrl -and (Test-Cli { az ad app update --id $appId --identifier-uris @($current + "api://$appId" | Select-Object -Unique) })) {
            $Failures.Add("$DisplayName`: Entra refused the MCP URL as an Application ID URI (api://<client id> was kept). The tenant policy accepts https URIs on verified domains only; v2.0-token apps are exempt by default. Put the MCP endpoint on a verified custom domain, or ask an administrator for an exemption (docs/11, step 2).")
        } else {
            throw 'az ad app update --identifier-uris failed'
        }
    } else {
        Write-Host '    identifier URIs already set'
    }

    $spId = Get-ServicePrincipalId $appId
    az ad sp update --id $spId --set appRoleAssignmentRequired=true; Assert-Ok 'az ad sp update'
    return @{ AppId = $appId; ObjectId = $objectId; SpId = $spId; ScopeId = $scopes[0].id; RoleValues = @($roles | ForEach-Object { $_.value }); RoleIds = @{}; Roles = $roles; Scope = $scopes[0].value; Name = $DisplayName; McpUrl = $McpUrl }
}

# The public client Claude Code and the hosted Claude apps sign in with. No secret can exist for it.
function Set-PublicClient([string]$DisplayName, [string[]]$RedirectUris) {
    Write-Host "== $DisplayName"
    $app = Find-App $DisplayName
    if ($null -eq $app) {
        $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --is-fallback-public-client true --public-client-redirect-uris @RedirectUris --query appId -o tsv
        Assert-Ok 'az ad app create'
        Write-Host '    created public client'
    } else {
        $appId = $app.appId
        $existing = @()
        $pc = Get-Field $app 'publicClient'
        if ($null -ne $pc) { $existing = @(Get-Field $pc 'redirectUris') }
        $union = @($existing + $RedirectUris | Where-Object { $_ } | Select-Object -Unique)
        if ($union.Count -ne $existing.Count) {
            az ad app update --id $appId --is-fallback-public-client true --public-client-redirect-uris @union; Assert-Ok 'az ad app update'
            Write-Host '    public client exists; redirect URIs added'
        } else {
            Write-Host '    public client exists; redirect URIs already present'
        }
    }
    return $appId
}

# Antigravity and Perplexity need a client secret. The app is created; the secret is yours to create and keep (Key Vault, the client's dialog).
function Set-ConfidentialClient([string]$DisplayName) {
    Write-Host "== $DisplayName"
    $app = Find-App $DisplayName
    $uris = @('https://antigravity.google/oauth-callback', 'https://www.perplexity.ai/rest/connections/oauth_callback', $ClaudeCallback)
    if ($null -eq $app) {
        $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --web-redirect-uris @uris --query appId -o tsv; Assert-Ok 'az ad app create'
        Write-Host '    created confidential client (no secret yet)'
    } else {
        $appId = $app.appId
        Write-Host '    confidential client exists (its secret is not touched)'
    }
    $Notes.Add("Confidential client secret: create it only when a client needs it, with `az ad app credential reset --id $appId --years 1 --query password -o tsv`, straight into Key Vault or the client's dialog; track its expiry.")
    return $appId
}

# Pre-authorizes the client apps on a server's scope (no consent prompt). PATCH replaces the list, so it is built in full each run.
function Set-PreAuthorization($Server, [string[]]$ClientAppIds) {
    $entries = @($ClientAppIds | Where-Object { $_ } | Select-Object -Unique | ForEach-Object { @{ appId = $_; delegatedPermissionIds = @($Server.ScopeId) } })
    $body = @{ api = @{ preAuthorizedApplications = $entries } } | ConvertTo-Json -Depth 6 -Compress
    $file = Write-WorkFile "preauth-$($Server.AppId).json" $body
    az rest --method PATCH --uri "$Graph/applications/$($Server.ObjectId)" --headers 'Content-Type=application/json' --body "@$file"; Assert-Ok 'az rest PATCH preAuthorizedApplications'
    Write-Host "    pre-authorized $($entries.Count) client app(s) on $($Server.Scope)"
}

function Get-RoleAssignmentList([string]$ServerSpId) {
    $json = az rest --method GET --uri "$Graph/servicePrincipals/$ServerSpId/appRoleAssignedTo?`$top=999" -o json; Assert-Ok 'az rest GET appRoleAssignedTo'
    $page = ConvertFrom-CliJson $json
    return @(Get-Field $page 'value')
}

# One assignment of one role to one principal (user or service principal); existing ones are kept.
function Set-RoleAssignment($Server, [string]$RoleValue, [string]$PrincipalId, [string]$Who) {
    $role = @($Server.Roles | Where-Object { $_.value -eq $RoleValue })
    if ($role.Count -ne 1) { throw "role $RoleValue is not in the definition file of $($Server.Name)" }
    $roleId = $role[0].id
    $have = @(Get-RoleAssignmentList $Server.SpId | Where-Object { $_.principalId -eq $PrincipalId -and $_.appRoleId -eq $roleId })
    if ($have.Count) { Write-Host "    $Who already holds $RoleValue"; return }
    $body = @{ principalId = $PrincipalId; resourceId = $Server.SpId; appRoleId = $roleId } | ConvertTo-Json -Compress
    $file = Write-WorkFile "assign-$roleId-$PrincipalId.json" $body
    $ok = Test-Cli { az rest --method POST --uri "$Graph/servicePrincipals/$($Server.SpId)/appRoleAssignedTo" --headers 'Content-Type=application/json' --body "@$file" }
    if ($ok) { Write-Host "    assigned $RoleValue to $Who" }
    else { $Failures.Add("could not assign $RoleValue to $Who on $($Server.Name): the signed-in account lacks AppRoleAssignment.ReadWrite.All. Ask an administrator to run: az rest --method POST --uri $Graph/servicePrincipals/$($Server.SpId)/appRoleAssignedTo --body '$body'") }
}

# Read-back: what Entra holds now, compared with the files and the switches. Adds to $Failures; prints PASS lines.
function Test-ServerApp($Server, [string[]]$ExpectedClients, [hashtable]$ExpectedAssignments) {
    $a = ConvertFrom-CliJson (az ad app show --id $Server.AppId -o json); Assert-Ok 'az ad app show'
    $roles = @($a.appRoles | ForEach-Object { $_.value } | Sort-Object)
    $wantRoles = @($Server.RoleValues | Sort-Object)
    if (($roles -join ',') -eq ($wantRoles -join ',')) { Write-Host "  PASS $($Server.Name): app roles $($roles -join ', ')" } else { $Failures.Add("$($Server.Name): app roles are $($roles -join ', '), expected $($wantRoles -join ', ')") }
    $scopes = @($a.api.oauth2PermissionScopes | ForEach-Object { $_.value })
    if ($scopes.Count -eq 1 -and $scopes[0] -eq $Server.Scope) { Write-Host "  PASS $($Server.Name): scope $($Server.Scope)" } else { $Failures.Add("$($Server.Name): scopes are $($scopes -join ', '), expected $($Server.Scope)") }
    if (@($roles | Where-Object { $scopes -contains $_ }).Count) { $Failures.Add("$($Server.Name): a role value equals a scope value (Entra keeps both in one collection)") }
    if ("$($a.api.requestedAccessTokenVersion)" -eq '2') { Write-Host "  PASS $($Server.Name): v2.0 tokens" } else { $Failures.Add("$($Server.Name): requestedAccessTokenVersion is not 2") }
    $uris = @($a.identifierUris)
    if ($uris -contains "api://$($Server.AppId)") { Write-Host "  PASS $($Server.Name): identifier URI api://<client id>" } else { $Failures.Add("$($Server.Name): api://<client id> is not an identifier URI") }
    if ($Server.McpUrl) {
        if ($uris -contains $Server.McpUrl.TrimEnd('/')) { Write-Host "  PASS $($Server.Name): identifier URI for the MCP URL" } else { $Failures.Add("$($Server.Name): the MCP URL is not an identifier URI (see the note above about verified domains)") }
    }
    $pre = @($a.api.preAuthorizedApplications | ForEach-Object { $_.appId } | Sort-Object)
    $wantPre = @($ExpectedClients | Where-Object { $_ } | Select-Object -Unique | Sort-Object)
    if (($pre -join ',') -eq ($wantPre -join ',')) { Write-Host "  PASS $($Server.Name): pre-authorized clients ($($pre.Count))" } else { $Failures.Add("$($Server.Name): pre-authorized clients are $($pre -join ', '), expected $($wantPre -join ', ')") }
    $required = az ad sp show --id $Server.SpId --query appRoleAssignmentRequired -o tsv; Assert-Ok 'az ad sp show'
    if ("$required" -eq 'true') { Write-Host "  PASS $($Server.Name): assignment required" } else { $Failures.Add("$($Server.Name): appRoleAssignmentRequired is not true") }
    $assignments = @(Get-RoleAssignmentList $Server.SpId)
    foreach ($key in $ExpectedAssignments.Keys) {
        $principal, $roleValue = $key.Split('|')
        $roleId = @($Server.Roles | Where-Object { $_.value -eq $roleValue })[0].id
        if (@($assignments | Where-Object { $_.principalId -eq $principal -and $_.appRoleId -eq $roleId }).Count) { Write-Host "  PASS $($Server.Name): $($ExpectedAssignments[$key]) holds $roleValue" }
        else { $Failures.Add("$($Server.Name): $($ExpectedAssignments[$key]) does not hold $roleValue") }
    }
}

try {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'az (Azure CLI) is not installed. Azure Cloud Shell has it.' }
    foreach ($uri in @($ResourceUri, $VaultResourceUri) | Where-Object { $_ }) {
        $parsed = $null
        if (-not ([Uri]::TryCreate($uri, [UriKind]::Absolute, [ref]$parsed)) -or $parsed.Scheme -ne 'https' -or $parsed.Query -or $parsed.Fragment) { throw "$uri is not an https URL without query or fragment." }
    }
    if ($PipelineClientId -and -not (Test-Guid $PipelineClientId)) { throw '-PipelineClientId must be the client (application) id of the deploy identity, a GUID (AZURE_CLIENT_ID).' }
    if ($CallbackPort -lt 1 -or $CallbackPort -gt 65535) { throw '-CallbackPort must be a TCP port.' }

    Write-Host '== Azure sign-in'
    $currentTenant = Get-CliOutput { az account show --query tenantId -o tsv }
    if (-not $currentTenant -or ($TenantId -and $currentTenant -ne $TenantId)) {
        Write-Host '  signing in with a device code: open https://microsoft.com/devicelogin and enter the code below (press Enter if asked for a subscription)'
        if ($TenantId) { az login --use-device-code --tenant $TenantId --allow-no-subscriptions | Out-Null } else { az login --use-device-code --allow-no-subscriptions | Out-Null }
        Assert-Ok 'az login'
    }
    $TenantId = az account show --query tenantId -o tsv; Assert-Ok 'az account show'
    $User = az account show --query user.name -o tsv; Assert-Ok 'az account show'
    Write-Host "  signed in to tenant $TenantId as $User"
    if (-not $AdminObjectId) {
        $AdminObjectId = Get-CliOutput { az ad signed-in-user show --query id -o tsv }
        if (-not $AdminObjectId) { throw 'The signed-in account is not a user (a service principal?). Pass -AdminObjectId with the object id of the person who gets Adr.Admin.' }
    }

    # 1. The servers.
    $servers = @()
    $adr = Set-ServerApp $ServerAppName 'app-roles.json' 'oauth2-permission-scopes.json' $ResourceUri
    $servers += $adr
    $vaultServer = $null
    if ($Vault) {
        $vaultServer = Set-ServerApp $VaultAppName 'vault-app-roles.json' 'vault-oauth2-permission-scopes.json' $VaultResourceUri
        $servers += $vaultServer
    }

    # 2. The clients.
    $publicClientId = Set-PublicClient $PublicClientName @("http://localhost:$CallbackPort/callback", $ClaudeCallback)
    $confidentialClientId = ''
    if ($ConfidentialClient) { $confidentialClientId = Set-ConfidentialClient $ConfidentialClientName }
    $clients = @($publicClientId)
    if ($confidentialClientId) { $clients += $confidentialClientId }
    if ($PreauthorizeAzureCli) { $clients += $AzureCliAppId }
    if ($PipelineClientId) { $clients += $PipelineClientId }   # an application needs no consent, but the allow-list and pre-authorization keep one list

    # 3. Pre-authorization and role assignments.
    foreach ($server in $servers) {
        Write-Host "== $($server.Name): clients and assignments"
        Set-PreAuthorization $server $clients
    }
    Set-RoleAssignment $adr 'Adr.Admin' $AdminObjectId 'you'
    $adrAssignments = @{ "$AdminObjectId|Adr.Admin" = 'you' }
    if ($PipelineClientId) {
        $pipelineSp = Get-CliOutput { az ad sp list --filter "appId eq '$PipelineClientId'" --query '[0].id' -o tsv }
        if (-not $pipelineSp) { throw "No service principal for -PipelineClientId in tenant $TenantId. Run scripts/setup-adr-deploy.ps1 first; it creates the deploy identity." }
        Set-RoleAssignment $adr 'Adr.Reader.Application' $pipelineSp 'the deploy pipeline'
        $adrAssignments["$pipelineSp|Adr.Reader.Application"] = 'the deploy pipeline'
    }
    $vaultAssignments = @{}
    if ($vaultServer) {
        Set-RoleAssignment $vaultServer 'Vault.Reader' $AdminObjectId 'you'
        $vaultAssignments["$AdminObjectId|Vault.Reader"] = 'you'
    }

    # 4. Read-back.
    Write-Host '== Read-back'
    Test-ServerApp $adr $clients $adrAssignments
    if ($vaultServer) { Test-ServerApp $vaultServer $clients $vaultAssignments }
    $pc = ConvertFrom-CliJson (az ad app show --id $publicClientId -o json); Assert-Ok 'az ad app show'
    $pcUris = @((Get-Field (Get-Field $pc 'publicClient') 'redirectUris'))
    $wantUris = @("http://localhost:$CallbackPort/callback", $ClaudeCallback)
    if (@($wantUris | Where-Object { $pcUris -notcontains $_ }).Count -eq 0) { Write-Host "  PASS ${PublicClientName}: redirect URIs" } else { $Failures.Add("${PublicClientName}: redirect URIs are $($pcUris -join ', ')") }
    if (@((Get-Field $pc 'passwordCredentials')).Count -eq 0 -and @((Get-Field $pc 'keyCredentials')).Count -eq 0) { Write-Host "  PASS ${PublicClientName}: no credential (a public client)" } else { $Failures.Add("${PublicClientName}: a credential exists on the public client; remove it") }

    foreach ($note in $Notes) { Write-Host "  NOTE $note" }
    if ($Failures.Count) {
        Write-Host ''
        foreach ($f in $Failures) { Write-Host "  FAIL $f" }
        throw "setup is not complete ($($Failures.Count) item(s) above)."
    }

    Write-Host ''
    Write-Host 'PASS: Entra setup verified. Values for the deployment (ids, not secrets):'
    Write-Host "  azd env set ADR_ENTRA_CLIENT_ID $($adr.AppId)"
    Write-Host "  azd env set ADR_ENTRA_ALLOWED_CLIENT_IDS `"$(($clients | Where-Object { $_ -ne $PipelineClientId }) -join ',')`""
    if ($PipelineClientId) { Write-Host '  GitHub repository variable ADR_ENTRA_PIPELINE_READ=true (the job passes AZURE_CLIENT_ID itself)' }
    if ($vaultServer) {
        Write-Host "  (src/VaultMcp) azd env set VAULT_AUTH_MODE Entra; azd env set VAULT_ENTRA_CLIENT_ID $($vaultServer.AppId); azd env set VAULT_ENTRA_ALLOWED_CLIENT_IDS `"$(($clients | Where-Object { $_ -ne $PipelineClientId }) -join ',')`""
    }
    Write-Host "  Claude Code: claude mcp add --transport http --client-id $publicClientId --callback-port $CallbackPort <name> <MCP URL>"
    Write-Host '  claude.ai / Desktop / phone: custom connector, "Use your own OAuth client", the same client id, secret blank'
    Write-Host 'Role assignments can take a few minutes to apply.'
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
