<#
.SYNOPSIS
  One-shot, re-runnable setup for the adr-dev deploy job (docs/07-tests-and-ci.md).
.DESCRIPTION
  Azure : deploy identity (app registration + service principal), GitHub OIDC federated credential,
          Contributor + Role Based Access Control Administrator limited to the roles infra/ assigns.
  GitHub: protected environment (you as required reviewer, main only) and repository variables.
  No secrets are created anywhere: GitHub signs in to Azure with OIDC.

  Safe to re-run. Once the repository variables exist they pin the deployment: the script signs in to the tenant in
  AZURE_TENANT_ID, stops if the app in AZURE_CLIENT_ID is not visible there (it never creates a second app), refuses a
  different subscription or region, keeps ADR_AUTH_MODE and ADR_PRIVATE_NETWORKING as they are, and leaves an
  environment that already has reviewers and a branch policy alone. It ends by reading everything back and prints
  PASS, or stops with what failed.

  Needs the Azure CLI (az) and GitHub CLI (gh). Azure Cloud Shell (PowerShell) has both; on Windows run
  `winget install Microsoft.AzureCLI GitHub.cli` once. Works in Windows PowerShell 5.1 and PowerShell 7.
  You need Owner (or User Access Administrator) on the subscription, permission to create app registrations,
  and admin on the GitHub repository.
  When az is not signed in to the right tenant, the script signs you in with a device code: open
  https://microsoft.com/devicelogin in any browser and enter the code it prints.
.EXAMPLE
  .\setup-adr-deploy.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000
.EXAMPLE
  .\setup-adr-deploy.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000 -TenantId 11111111-1111-1111-1111-111111111111
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SubscriptionId,
    [string]$TenantId = '',                 # first run, when your account spans several tenants; later runs use AZURE_TENANT_ID
    [string]$Location = 'southcentralus',   # first run; later runs keep AZURE_LOCATION
    [string]$Repo = 'saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP',   # owner/name; the OIDC subject itself is read from GitHub
    [string]$Environment = 'adr-dev',
    [string]$AppName = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $AppName) { $AppName = "adr-mcp-deploy-$Environment" }
$Scope = "/subscriptions/$SubscriptionId"
$Issuer = 'https://token.actions.githubusercontent.com'
$Audience = 'api://AzureADTokenExchange'
$CredentialName = "github-$Environment"
$RbacAdmin = 'Role Based Access Control Administrator'

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

# stdout of a native command that may fail (for example a 404), or '' when it does; stderr is discarded.
function Get-CliOutput([scriptblock]$Command) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = & $Command 2> $null; if ($LASTEXITCODE -eq 0) { return ("$out").Trim() } return '' } finally { $ErrorActionPreference = $previous }
}

# JSON from a CLI. foreach flattens arrays the same way in Windows PowerShell 5.1 (which returns them as one object) and 7.
function ConvertFrom-CliJson([string[]]$Lines) {
    foreach ($item in (($Lines | Out-String) | ConvertFrom-Json)) { $item }
}

# A field the API may omit, without tripping Set-StrictMode.
function Get-Field($Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name }
}

# ARM may reformat whitespace in a stored condition; GUIDs and action names compare case-insensitively.
function Test-SameCondition([string]$A, [string]$B) { ($A -replace '\s', '') -eq ($B -replace '\s', '') }

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

    # Variables from an earlier run pin the tenant, subscription, region and deploy app.
    $json = gh variable list --repo $Repo --json 'name,value'; Assert-Ok 'gh variable list'
    $Vars = @{}
    foreach ($variable in (ConvertFrom-CliJson $json)) { $Vars[$variable.name] = $variable.value }
    if ($Vars['AZURE_SUBSCRIPTION_ID'] -and $Vars['AZURE_SUBSCRIPTION_ID'] -ne $SubscriptionId) {
        throw "$Repo deploys to subscription $($Vars['AZURE_SUBSCRIPTION_ID']) (AZURE_SUBSCRIPTION_ID). Pass that one, or delete the variable first to move the environment."
    }
    if ($TenantId -and $Vars['AZURE_TENANT_ID'] -and $TenantId -ne $Vars['AZURE_TENANT_ID']) {
        throw "GitHub signs in to tenant $($Vars['AZURE_TENANT_ID']) (AZURE_TENANT_ID), not $TenantId."
    }
    $ExpectedTenant = if ($TenantId) { $TenantId } else { $Vars['AZURE_TENANT_ID'] }
    if ($Vars['AZURE_LOCATION']) {
        if ($PSBoundParameters.ContainsKey('Location') -and $Location -ne $Vars['AZURE_LOCATION']) {
            throw "$Environment is deployed in $($Vars['AZURE_LOCATION']) (AZURE_LOCATION); moving it to $Location means a new environment."
        }
        $Location = $Vars['AZURE_LOCATION']
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
    $currentTenant = Get-CliOutput { az account show --query tenantId -o tsv }
    if (-not $currentTenant -or ($ExpectedTenant -and $currentTenant -ne $ExpectedTenant)) {
        Write-Host '  signing in with a device code: open https://microsoft.com/devicelogin and enter the code below'
        if ($ExpectedTenant) { az login --use-device-code --tenant $ExpectedTenant | Out-Null } else { az login --use-device-code | Out-Null }
        Assert-Ok 'az login --use-device-code'
    }
    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) { throw "Subscription $SubscriptionId is not visible to the signed-in account. Sign in with an account that is Owner on it." }
    $TenantId = az account show --query tenantId -o tsv; Assert-Ok 'az account show'
    $User = az account show --query user.name -o tsv; Assert-Ok 'az account show'
    if ($ExpectedTenant -and $TenantId -ne $ExpectedTenant) {
        throw "Subscription $SubscriptionId is in tenant $TenantId, but GitHub signs in to tenant $ExpectedTenant."
    }
    Write-Host "  signed in to tenant $TenantId as $User"

    $regions = az functionapp list-flexconsumption-locations --query '[].name' -o tsv; Assert-Ok 'az functionapp list-flexconsumption-locations'
    $normalized = @($regions | ForEach-Object { ($_ -replace '\s', '').ToLowerInvariant() })
    if ($normalized -notcontains $Location.ToLowerInvariant()) {
        throw "Flex Consumption is not offered in $Location. Pick one from: az functionapp list-flexconsumption-locations -o table"
    }
    Write-Host "  region $Location supports Flex Consumption"

    if ($Vars['AZURE_CLIENT_ID']) {
        # The app GitHub already signs in as. Not finding it means the wrong tenant or account, never "create another".
        $AppId = $Vars['AZURE_CLIENT_ID']
        az ad app show --id $AppId --query appId -o tsv | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "The deploy app GitHub uses ($AppId, AZURE_CLIENT_ID) is not visible to $User in tenant $TenantId. Sign in with the account that owns it: az login --tenant $TenantId --use-device-code"
        }
        Write-Host "  app registration $AppId (AZURE_CLIENT_ID) found"
    } else {
        $found = @(az ad app list --filter "displayName eq '$AppName'" --query '[].appId' -o tsv); Assert-Ok 'az ad app list'
        if ($found.Count -gt 1) { throw "Several app registrations are named $AppName. Pass -AppName with a unique name." }
        if ($found.Count -eq 1) {
            $AppId = $found[0]
            Write-Host "  app registration $AppName exists"
        } else {
            $AppId = az ad app create --display-name $AppName --query appId -o tsv; Assert-Ok 'az ad app create'
            Write-Host "  created app registration $AppName"
        }
    }
    $SpId = az ad sp list --filter "appId eq '$AppId'" --query '[0].id' -o tsv; Assert-Ok 'az ad sp list'
    if (-not $SpId) {
        $SpId = az ad sp create --id $AppId --query id -o tsv; Assert-Ok 'az ad sp create'
        Write-Host '  created service principal'
    }

    function Get-FederatedCredentials {
        $json = az ad app federated-credential list --id $AppId -o json; Assert-Ok 'az ad app federated-credential list'
        ConvertFrom-CliJson $json
    }
    $credentials = @(Get-FederatedCredentials)
    if (@($credentials | Where-Object { $_.subject -ceq $Subject -and $_.issuer -ceq $Issuer }).Count) {
        Write-Host "  federated credential for $Subject exists"
    } else {
        $credential = @{ issuer = $Issuer; subject = $Subject; audiences = @($Audience) }
        # A credential with our name but another subject (e.g. the pre-immutable format) is corrected in place.
        $named = @($credentials | Where-Object { $_.name -eq $CredentialName })
        if ($named.Count) {
            $credentialFile = Write-WorkFile 'federated-credential.json' ($credential | ConvertTo-Json -Compress)   # name can't be changed
            az ad app federated-credential update --id $AppId --federated-credential-id $named[0].id --parameters "@$credentialFile" | Out-Null
            Assert-Ok 'az ad app federated-credential update'
            Write-Host "  updated federated credential $CredentialName to $Subject"
        } else {
            $credential.name = $CredentialName
            $credentialFile = Write-WorkFile 'federated-credential.json' ($credential | ConvertTo-Json -Compress)
            az ad app federated-credential create --id $AppId --parameters "@$credentialFile" | Out-Null; Assert-Ok 'az ad app federated-credential create'
            Write-Host "  created federated credential for $Subject"
        }
    }

    function Get-RoleAssignments([string]$Role) {
        $json = az role assignment list --assignee $SpId --role $Role --scope $Scope -o json; Assert-Ok "az role assignment list ($Role)"
        ConvertFrom-CliJson $json
    }
    # Idempotent. For a constrained role, every assignment without the condition is removed: one would undo the limit.
    function Set-RoleAssignment([string]$Role, [string]$RequiredCondition = '') {
        $assignments = @(Get-RoleAssignments $Role)
        if (-not $RequiredCondition) {
            if ($assignments.Count) { Write-Host "  ${Role}: already assigned"; return }
            az role assignment create --assignee-object-id $SpId --assignee-principal-type ServicePrincipal --role $Role --scope $Scope | Out-Null
            Assert-Ok "az role assignment create ($Role)"
            Write-Host "  ${Role}: assigned"
            return
        }
        $kept = $false
        foreach ($assignment in $assignments) {
            if (-not $kept -and (Test-SameCondition (Get-Field $assignment 'condition') $RequiredCondition)) { $kept = $true; continue }
            az role assignment delete --ids $assignment.id; Assert-Ok "az role assignment delete ($Role)"
            Write-Host "  ${Role}: removed an assignment without the required condition"
        }
        if ($kept) { Write-Host "  ${Role}: already assigned, limited to the roles in infra/modules/rbac.bicep"; return }
        $conditionFile = Write-WorkFile 'condition.txt' $RequiredCondition
        az role assignment create --assignee-object-id $SpId --assignee-principal-type ServicePrincipal --role $Role --scope $Scope `
            --condition "@$conditionFile" --condition-version 2.0 | Out-Null; Assert-Ok "az role assignment create ($Role)"
        Write-Host "  ${Role}: assigned, limited to the roles in infra/modules/rbac.bicep"
    }
    Set-RoleAssignment 'Contributor'
    Set-RoleAssignment $RbacAdmin $Condition

    Write-Host '== GitHub environment and variables'
    function Get-DeployEnvironment {
        $json = Get-CliOutput { gh api "repos/$Repo/environments/$Environment" }
        if ($json) { $json | ConvertFrom-Json }
    }
    function Get-ReviewerIds($EnvironmentObject) {
        foreach ($rule in @(Get-Field $EnvironmentObject 'protection_rules')) {
            if ((Get-Field $rule 'type') -eq 'required_reviewers') { foreach ($r in @(Get-Field $rule 'reviewers')) { (Get-Field $r 'reviewer').id } }
        }
    }
    $existingEnvironment = Get-DeployEnvironment
    $reviewerIds = @(Get-ReviewerIds $existingEnvironment)
    $branchPolicy = Get-Field $existingEnvironment 'deployment_branch_policy'
    if ($reviewerIds.Count -and (Get-Field $branchPolicy 'custom_branch_policies')) {
        Write-Host "  environment ${Environment}: kept as it is ($($reviewerIds.Count) required reviewer(s), custom branch policy)"
    } else {
        # Created, or completed: existing reviewers, wait timer and self-review setting are carried over.
        $ReviewerId = gh api user --jq .id; Assert-Ok 'gh api user'
        $reviewers = @(foreach ($rule in @(Get-Field $existingEnvironment 'protection_rules')) {
            if ((Get-Field $rule 'type') -eq 'required_reviewers') {
                foreach ($r in @(Get-Field $rule 'reviewers')) { @{ type = $r.type; id = [long](Get-Field $r 'reviewer').id } }
            }
        })
        if (-not ($reviewers | Where-Object { $_.id -eq [long]$ReviewerId })) { $reviewers += @{ type = 'User'; id = [long]$ReviewerId } }
        $body = @{ reviewers = $reviewers; deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true } }
        foreach ($rule in @(Get-Field $existingEnvironment 'protection_rules')) {
            if ((Get-Field $rule 'type') -eq 'wait_timer') { $body.wait_timer = $rule.wait_timer }
            if ((Get-Field $rule 'type') -eq 'required_reviewers' -and (Get-Field $rule 'prevent_self_review')) { $body.prevent_self_review = $true }
        }
        $environmentFile = Write-WorkFile 'environment.json' ($body | ConvertTo-Json -Depth 5 -Compress)
        gh api -X PUT "repos/$Repo/environments/$Environment" --input $environmentFile | Out-Null; Assert-Ok 'gh api PUT environment'
        Write-Host "  environment ${Environment}: you are a required reviewer; deployments only from allowed branches"
    }
    $policies = @(gh api "repos/$Repo/environments/$Environment/deployment-branch-policies" --jq '.branch_policies[].name'); Assert-Ok 'gh api branch policies'
    if ($policies -notcontains 'main') {
        gh api -X POST "repos/$Repo/environments/$Environment/deployment-branch-policies" -f name=main -f type=branch | Out-Null
        Assert-Ok 'gh api POST branch policy'
        Write-Host "  environment ${Environment}: main added to the deployment branches"
    }

    # Repository variables. The deploy job's 'if' sees repository variables only, and environment variables would
    # override these inside the job, so environment-level copies of the same names are removed.
    $environmentVariables = @(gh variable list --repo $Repo --env $Environment --json name --jq '.[].name'); Assert-Ok 'gh variable list'
    function Set-RepoVariable([string]$Name, [string]$Value) {
        if ($Vars[$Name] -ceq $Value) {
            Write-Host "  $Name = $Value (unchanged)"
        } else {
            gh variable set $Name --repo $Repo --body $Value; Assert-Ok "gh variable set $Name"
            Write-Host "  $Name = $Value"
        }
        if ($environmentVariables -contains $Name) {
            gh variable delete $Name --repo $Repo --env $Environment; Assert-Ok "gh variable delete $Name"
            Write-Host "  ${Name}: removed the $Environment environment copy"
        }
    }
    # Auth mode and private networking are deployment choices: defaults on the first run, kept as they are afterwards.
    $Expected = [ordered]@{
        AZURE_CLIENT_ID        = $AppId
        AZURE_TENANT_ID        = $TenantId
        AZURE_SUBSCRIPTION_ID  = $SubscriptionId
        AZURE_LOCATION         = $Location
        ADR_AUTH_MODE          = $(if ($Vars['ADR_AUTH_MODE']) { $Vars['ADR_AUTH_MODE'] } else { 'FunctionKey' })
        ADR_PRIVATE_NETWORKING = $(if ($Vars['ADR_PRIVATE_NETWORKING']) { $Vars['ADR_PRIVATE_NETWORKING'] } else { 'false' })
        ADR_DEPLOY_ENABLED     = 'true'   # last: from now on every push to main deploys (after your approval)
    }
    foreach ($name in $Expected.Keys) { Set-RepoVariable $name $Expected[$name] }

    $secrets = @(gh secret list --repo $Repo --json name --jq '.[].name'); Assert-Ok 'gh secret list'
    if ($secrets -contains 'ADR_DEPLOY_ENABLED') {
        Write-Host '  note: a SECRET named ADR_DEPLOY_ENABLED also exists; the workflow ignores it (it reads the variable). You can delete it.'
    }

    # Read everything back from Entra, Azure and GitHub, so a step that silently did nothing cannot pass.
    Write-Host '== Verify'
    $failures = @()
    if (@(Get-FederatedCredentials | Where-Object { $_.subject -ceq $Subject -and $_.issuer -ceq $Issuer -and $_.audiences -contains $Audience }).Count) {
        Write-Host "  PASS federated credential: $Subject"
    } else { $failures += "no federated credential on app $AppId matches $Subject" }
    if (@(Get-RoleAssignments 'Contributor').Count) { Write-Host '  PASS Contributor' } else { $failures += 'Contributor is not assigned' }
    $admin = @(Get-RoleAssignments $RbacAdmin)
    if ($admin.Count -eq 1 -and (Test-SameCondition (Get-Field $admin[0] 'condition') $Condition)) {
        Write-Host "  PASS ${RbacAdmin}: one assignment, with the condition"
    } else { $failures += "$RbacAdmin must have exactly one assignment, with the condition (found $($admin.Count))" }
    $deployEnvironment = Get-DeployEnvironment
    $policies = @(gh api "repos/$Repo/environments/$Environment/deployment-branch-policies" --jq '.branch_policies[].name'); Assert-Ok 'gh api branch policies'
    if (@(Get-ReviewerIds $deployEnvironment).Count -and (Get-Field (Get-Field $deployEnvironment 'deployment_branch_policy') 'custom_branch_policies') -and $policies -contains 'main') {
        Write-Host "  PASS environment ${Environment}: required reviewers, main in the branch policy"
    } else { $failures += "environment $Environment lacks required reviewers or the main branch policy" }
    $json = gh variable list --repo $Repo --json 'name,value'; Assert-Ok 'gh variable list'
    $actual = @{}
    foreach ($variable in (ConvertFrom-CliJson $json)) { $actual[$variable.name] = $variable.value }
    $leftovers = @(gh variable list --repo $Repo --env $Environment --json name --jq '.[].name'); Assert-Ok 'gh variable list'
    $wrong = @($Expected.Keys | Where-Object { $actual[$_] -cne $Expected[$_] -or $leftovers -contains $_ })
    if ($wrong.Count) { $failures += "repository variables not as expected: $($wrong -join ', ')" } else { Write-Host '  PASS repository variables' }
    if ($failures.Count) { throw "Verification failed:`n  - $($failures -join "`n  - ")" }

    Write-Host 'PASS: setup verified. Role assignments can take a few minutes to apply.'
    Write-Host "Every push to main now deploys after your approval. To deploy now: gh workflow run ci-cd.yml --repo $Repo --ref main -f environment=$Environment"
}
finally {
    Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
}
