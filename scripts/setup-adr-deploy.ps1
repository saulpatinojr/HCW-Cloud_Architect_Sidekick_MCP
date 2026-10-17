<#
.SYNOPSIS
  One-shot, re-runnable setup for the adr-dev deploy job (docs/07-tests-and-ci.md).
.DESCRIPTION
  Azure : deploy identity (app registration + service principal), GitHub OIDC federated credential,
          Contributor + Role Based Access Control Administrator limited to the roles infra/ assigns.
  GitHub: protected environment (you as required reviewer, main only) and repository variables.
  No secrets are created anywhere: GitHub signs in to Azure with OIDC.

  Needs the Azure CLI (az) and GitHub CLI (gh). Azure Cloud Shell (PowerShell) has both; on Windows run
  `winget install Microsoft.AzureCLI GitHub.cli` once. Works in Windows PowerShell 5.1 and PowerShell 7.
  You need Owner (or User Access Administrator) on the subscription, permission to create app registrations,
  and admin on the GitHub repository.
  If az is not signed in, the script signs you in with a device code: open https://microsoft.com/devicelogin
  in any browser and enter the code it prints. Pass -TenantId when your account spans several tenants.
.EXAMPLE
  .\setup-adr-deploy.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000
.EXAMPLE
  .\setup-adr-deploy.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000 -TenantId 11111111-1111-1111-1111-111111111111
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SubscriptionId,
    [string]$TenantId = '',   # only needed for device-code sign-in when your account spans several tenants
    [string]$Location = 'southcentralus',
    [string]$Repo = 'saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP',   # owner/name; the OIDC subject itself is read from GitHub
    [string]$Environment = 'adr-dev',
    [string]$AppName = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $AppName) { $AppName = "adr-mcp-deploy-$Environment" }
$Scope = "/subscriptions/$SubscriptionId"

# Role definition IDs that infra/modules/rbac.bicep assigns. The deploy identity may grant these and nothing else.
$AssignableRoles = @(
    'ba92f5b4-2d11-453d-a403-e96b0029c9fe'   # Storage Blob Data Contributor
    'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'   # Storage Blob Data Owner
    'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'   # Storage Blob Delegator
    '974c5e8b-45b9-4653-ba55-5f855dd0fb88'   # Storage Queue Data Contributor
    '8a0f0c08-91a1-4084-bc3d-661d67233fed'   # Storage Queue Data Message Processor
    '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'   # Storage Table Data Contributor
    '4633458b-17de-408a-b874-0445c86b69e6'   # Key Vault Secrets User
    'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'   # Key Vault Secrets Officer
    '3913510d-42f4-4e42-8a64-420c390055eb'   # Monitoring Metrics Publisher
) -join ','
$Condition = "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals{$AssignableRoles})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals{$AssignableRoles}))"

# Native commands don't honour $ErrorActionPreference, so every call is checked explicitly.
function Assert-Ok([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

# True when the native command exits 0. stderr is discarded; Windows PowerShell 5.1 would otherwise turn it into errors.
function Test-Cli([scriptblock]$Command) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Command *> $null; return ($LASTEXITCODE -eq 0) } finally { $ErrorActionPreference = $previous }
}

# Values with quotes, braces or JSON go to the CLIs through files (az: @file, gh: --input), which avoids
# argument-quoting differences between PowerShell versions and az.cmd on Windows. No secrets are written.
$Work = Join-Path ([IO.Path]::GetTempPath()) ("adr-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
function Write-WorkFile([string]$Name, [string]$Content) {
    $path = Join-Path $Work $Name
    [IO.File]::WriteAllText($path, $Content)   # UTF-8 without BOM in both PowerShell versions
    return $path
}

try {
    foreach ($tool in 'az', 'gh') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not installed (Azure Cloud Shell has both)." }
    }

    Write-Host "== GitHub ($Repo)"
    if (-not (Test-Cli { gh auth status })) {
        # gh's web flow is also a one-time code: it prints the code and https://github.com/login/device.
        gh auth login --hostname github.com --git-protocol https --web; Assert-Ok 'gh auth login'
    }

    # Entra matches the federated credential's subject exactly, so it is taken from GitHub rather than built here.
    # Repositories created after 2026-07-15 get immutable subjects (repo:owner@<owner-id>/name@<repo-id>);
    # older ones keep repo:owner/name.
    $useDefault = gh api "repos/$Repo/actions/oidc/customization/sub" --jq '.use_default'; Assert-Ok 'gh api OIDC subject template'
    if ($useDefault -eq 'false') {
        throw "$Repo uses a custom OIDC subject template; create the federated credential for that template by hand."
    }
    $subjectPrefix = gh api "repos/$Repo/actions/oidc/customization/sub" --jq '.sub_claim_prefix // empty'; Assert-Ok 'gh api OIDC subject template'
    if (-not $subjectPrefix) { $subjectPrefix = "repo:$Repo" }
    $Subject = "${subjectPrefix}:environment:$Environment"
    Write-Host "  OIDC subject: $Subject"

    Write-Host '== Azure'
    if (-not (Test-Cli { az account show })) {
        Write-Host '  signing in with a device code: open https://microsoft.com/devicelogin and enter the code below'
        if ($TenantId) { az login --use-device-code --tenant $TenantId | Out-Null } else { az login --use-device-code | Out-Null }
        Assert-Ok 'az login --use-device-code'
    }
    az account set --subscription $SubscriptionId; Assert-Ok 'az account set'
    $TenantId = az account show --query tenantId -o tsv; Assert-Ok 'az account show'

    $regions = az functionapp list-flexconsumption-locations --query '[].name' -o tsv; Assert-Ok 'az functionapp list-flexconsumption-locations'
    $normalized = @($regions | ForEach-Object { ($_ -replace '\s', '').ToLowerInvariant() })
    if ($normalized -notcontains $Location.ToLowerInvariant()) {
        throw "Flex Consumption is not offered in $Location. Pick one from: az functionapp list-flexconsumption-locations -o table"
    }
    Write-Host "  region $Location supports Flex Consumption"

    $AppId = az ad app list --filter "displayName eq '$AppName'" --query '[0].appId' -o tsv; Assert-Ok 'az ad app list'
    if (-not $AppId) {
        $AppId = az ad app create --display-name $AppName --query appId -o tsv; Assert-Ok 'az ad app create'
        Write-Host "  created app registration $AppName"
    } else {
        Write-Host "  app registration $AppName exists"
    }
    $SpId = az ad sp list --filter "appId eq '$AppId'" --query '[0].id' -o tsv; Assert-Ok 'az ad sp list'
    if (-not $SpId) {
        $SpId = az ad sp create --id $AppId --query id -o tsv; Assert-Ok 'az ad sp create'
        Write-Host '  created service principal'
    }

    $CredentialName = "github-$Environment"
    $existing = az ad app federated-credential list --id $AppId --query "[?subject=='$Subject'].name" -o tsv; Assert-Ok 'az ad app federated-credential list'
    if (-not $existing) {
        $credential = @{
            issuer    = 'https://token.actions.githubusercontent.com'
            subject   = $Subject
            audiences = @('api://AzureADTokenExchange')
        }
        # A credential with our name but another subject (e.g. the pre-immutable format) is corrected in place.
        $staleId = az ad app federated-credential list --id $AppId --query "[?name=='$CredentialName'].id" -o tsv; Assert-Ok 'az ad app federated-credential list'
        if ($staleId) {
            $credentialFile = Write-WorkFile 'federated-credential.json' ($credential | ConvertTo-Json -Compress)   # name can't be changed
            az ad app federated-credential update --id $AppId --federated-credential-id $staleId --parameters "@$credentialFile" | Out-Null
            Assert-Ok 'az ad app federated-credential update'
            Write-Host "  updated federated credential $CredentialName to $Subject"
        } else {
            $credential.name = $CredentialName
            $credentialFile = Write-WorkFile 'federated-credential.json' ($credential | ConvertTo-Json -Compress)
            az ad app federated-credential create --id $AppId --parameters "@$credentialFile" | Out-Null; Assert-Ok 'az ad app federated-credential create'
            Write-Host "  created federated credential for $Subject"
        }
    } else {
        Write-Host "  federated credential for $Subject exists"
    }

    # Idempotent; an existing assignment without the required condition is replaced by a constrained one.
    function Set-RoleAssignment([string]$Role, [string]$RequiredCondition = '') {
        $id = az role assignment list --assignee $SpId --role $Role --scope $Scope --query '[0].id' -o tsv; Assert-Ok "az role assignment list ($Role)"
        if ($id) {
            $current = az role assignment list --assignee $SpId --role $Role --scope $Scope --query '[0].condition' -o tsv; Assert-Ok "az role assignment list ($Role)"
            if (-not $RequiredCondition -or $current -eq $RequiredCondition) { Write-Host "  ${Role}: already assigned"; return }
            az role assignment delete --ids $id; Assert-Ok "az role assignment delete ($Role)"
            Write-Host "  ${Role}: removed the unconstrained assignment"
        }
        if ($RequiredCondition) {
            $conditionFile = Write-WorkFile 'condition.txt' $RequiredCondition
            az role assignment create --assignee-object-id $SpId --assignee-principal-type ServicePrincipal --role $Role --scope $Scope `
                --condition "@$conditionFile" --condition-version 2.0 | Out-Null; Assert-Ok "az role assignment create ($Role)"
            Write-Host "  ${Role}: assigned, limited to the roles in infra/modules/rbac.bicep"
        } else {
            az role assignment create --assignee-object-id $SpId --assignee-principal-type ServicePrincipal --role $Role --scope $Scope | Out-Null
            Assert-Ok "az role assignment create ($Role)"
            Write-Host "  ${Role}: assigned"
        }
    }
    Set-RoleAssignment 'Contributor'
    Set-RoleAssignment 'Role Based Access Control Administrator' $Condition

    Write-Host '== GitHub environment and variables'
    $ReviewerId = gh api user --jq .id; Assert-Ok 'gh api user'
    $environmentBody = @{
        reviewers                = @(@{ type = 'User'; id = [long]$ReviewerId })
        deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
    } | ConvertTo-Json -Depth 5 -Compress
    $environmentFile = Write-WorkFile 'environment.json' $environmentBody
    gh api -X PUT "repos/$Repo/environments/$Environment" --input $environmentFile | Out-Null; Assert-Ok 'gh api PUT environment'
    $policies = @(gh api "repos/$Repo/environments/$Environment/deployment-branch-policies" --jq '.branch_policies[].name'); Assert-Ok 'gh api branch policies'
    if ($policies -notcontains 'main') {
        gh api -X POST "repos/$Repo/environments/$Environment/deployment-branch-policies" -f name=main -f type=branch | Out-Null
        Assert-Ok 'gh api POST branch policy'
    }
    Write-Host "  environment ${Environment}: you are the required reviewer; deployments only from main"

    # Repository variables. The deploy job's 'if' sees repository variables only, and environment variables would
    # override these inside the job, so environment-level copies of the same names are removed.
    $environmentVariables = @(gh variable list --repo $Repo --env $Environment --json name --jq '.[].name'); Assert-Ok 'gh variable list'
    function Set-RepoVariable([string]$Name, [string]$Value) {
        gh variable set $Name --repo $Repo --body $Value; Assert-Ok "gh variable set $Name"
        if ($environmentVariables -contains $Name) {
            gh variable delete $Name --repo $Repo --env $Environment; Assert-Ok "gh variable delete $Name"
            Write-Host "  ${Name}: removed the $Environment environment copy"
        }
        Write-Host "  $Name = $Value"
    }
    Set-RepoVariable 'AZURE_CLIENT_ID' $AppId
    Set-RepoVariable 'AZURE_TENANT_ID' $TenantId
    Set-RepoVariable 'AZURE_SUBSCRIPTION_ID' $SubscriptionId
    Set-RepoVariable 'AZURE_LOCATION' $Location
    Set-RepoVariable 'ADR_AUTH_MODE' 'FunctionKey'
    Set-RepoVariable 'ADR_PRIVATE_NETWORKING' 'false'
    Set-RepoVariable 'ADR_DEPLOY_ENABLED' 'true'   # last: from now on every push to main deploys (after your approval)

    $secrets = @(gh secret list --repo $Repo --json name --jq '.[].name'); Assert-Ok 'gh secret list'
    if ($secrets -contains 'ADR_DEPLOY_ENABLED') {
        Write-Host '  note: a SECRET named ADR_DEPLOY_ENABLED also exists; the workflow ignores it (it reads the variable). You can delete it.'
    }

    Write-Host 'Done. Role assignments can take a few minutes to apply.'
    Write-Host "First deploy: gh workflow run ci-cd.yml --repo $Repo --ref main -f environment=$Environment"
}
finally {
    Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
}
