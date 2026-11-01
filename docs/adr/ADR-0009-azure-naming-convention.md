# ADR-0009: One Azure naming convention for the resource group, every resource and the tags

- Status: Proposed (the decisions below were recorded on 2026-10-26 at the owner's request; the owner can veto them in the review of the pull request; after that review the owner changes the status to Accepted)
- Date: 2026-10-26
- Owners: Platform architecture
- Tags: azure, naming, tags, bicep, governance

## Context
The owner asked for one standard Azure naming convention for the resource group, every resource and the tags, and for the repository to be tidied. The server is deployed with the Azure Developer CLI (`azd`) and Bicep (Azure's template language) by the `ci-cd` workflow, which signs in with OpenID Connect (OIDC), so no secret is stored.

**Observed in this repository (2026-10-26):**
- `infra/main.bicep` built every name as `<abbreviation>-adr-<token>` with `token = toLower(uniqueString(subscription().id, environmentName, location))`, and the resource group as `rg-<environmentName>`. `infra/abbreviations.json` had 10 entries, each with a trailing hyphen except `st`.
- Tags were `azd-env-name` and `workload` (value `adr-mcp`) only.
- The unit test `DeploymentConfigurationTests` used `kv-adr` inside an unresolved Key Vault reference. It asserts that the app refuses to start with that text as the salt, so the vault name in it is only an example; this change updates it to the new vault name.

**Observed in Azure (2026-10-26, read-only `az` calls on the demo subscription; no ids are recorded here):**
- Resource group `rg-adr-dev` in `southcentralus` holds the eight resources the template creates (function app, App Service plan, Cosmos DB account, Key Vault, storage account, Log Analytics workspace, Application Insights, managed identity), each named with the hash token, plus the `Application Insights Smart Detection` action group that Azure creates by itself. Every resource the template creates, and the group, carry only the two tags above (the action group Azure creates has none).
- The function app's default host name is `<function app name>.azurewebsites.net`, so the function app name is public.
- The Key Vault has purge protection on and a 90 day soft-delete period. The `evidence` container has an unlocked immutability policy.
- The environment runs in `EntraBuiltIn` mode (repository variable `ADR_AUTH_MODE` and the app setting `Auth__Mode` both say so), and the Entra app registration of the server has the old function app's MCP URL as one of its Application ID URIs (the address Entra ID treats as the identity of the server). [ADR-0008](ADR-0008-entra-oauth-sign-in-for-mcp-clients.md), `docs/11-entra-sign-in.md` and the changelog, all written on 2026-10-25, still say adr-dev stays on `FunctionKey` and list "the tenant accepts a `*.azurewebsites.net` Application ID URI" as not verified; the registered URI answers that question for this tenant, and the change of mode is not recorded in the repository. Those texts are not edited here; the owner decides whether to update them.
- `az functionapp list-flexconsumption-locations` lists 52 regions, `southcentralus` among them.

**Microsoft guidance (read 2026-10-26):**
- [Define your naming convention](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming): most names cannot be changed after creation, so put only permanent facts in a name and the rest in tags; a name is made of resource type, workload, environment, region and instance number; a resource group is `rg-<workload>-<component, optional>-<environment, optional>`.
- [Abbreviation recommendations](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations): the published abbreviation for each resource type.
- [Naming rules and restrictions](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules): length, characters and scope for each resource type.
- No Microsoft page lists region short codes for resource names. Two community lists ([AzureNamer](https://azurenamingconventions.com/blog/azure-region-abbreviations/), [cloud-region-shortener](https://github.com/krsche/cloud-region-shortener)) agree on the codes used below. For 14 of the 16 regions the code is also what Microsoft prints as the gateway region code in the [Azure Virtual Desktop Insights glossary](https://learn.microsoft.com/azure/virtual-desktop/insights-glossary#gateway-region-codes); that page does not list West US 2 and West US 3.

**Not verified:** that the new global names are free in Azure (only a deployment tells); that an Azure Policy in the tenant accepts the names or tags; the host name format of the new function app (the old app uses the classic `<name>.azurewebsites.net`); whether Flex Consumption accepts the secure unique default host name setting (alternative 8); what `azd down --purge` does with a vault that has purge protection (the runbook avoids `--purge`).

## Decision
1. **Pattern.** `<abbreviation>-<workload>[-<component>]-<environment>-<region code>-<instance>`, for example `kv-adr-dev-scus-01`. The abbreviation comes from Microsoft's published list. The component sits after the workload, as in Microsoft's resource group form, and appears only where one resource type occurs several times (private endpoints, subnets). The instance number is fixed at `01`.
2. **Values for this workload.** Workload `adr`, environment `dev`, region code `scus` (South Central US). The storage account name cannot hold hyphens, so it is `stadrdevscus01`.
3. **Parts are separate from azd's environment name.** New Bicep parameters `workloadName` (default `adr`) and `environmentShort` (default `dev`), set by the azd variables `ADR_WORKLOAD_NAME` and `ADR_ENVIRONMENT_SHORT`. The azd environment name (`adr-dev`) is unchanged and is no longer part of any name: it stays the azd environment, the GitHub environment, the OIDC subject and the value of the `azd-env-name` tag. Because it is not in the names, two azd environments with the same `environmentShort` would get identical names in one subscription and region and overwrite each other. So Bicep refuses an `environmentName` that does not end with `-<environmentShort>` (`adr-dev` with `dev`): a second environment is named `adr-prod` and sets `ADR_ENVIRONMENT_SHORT=prod`, and the workflow needs its own `ADR_ENVIRONMENT_SHORT` line for it. This catches the likely mistake (a second environment that keeps the default `dev`). It does not stop two environments that end in the same short name, such as `adr-dev` and `other-dev`; those need a different `workloadName`, region or suffix.
4. **Global names and the suffix.** Four names are unique across all of Azure: the function app (it is the public host name), the Cosmos DB account, the Key Vault and the storage account. They take an optional parameter `nameSuffix` (default empty, azd variable `ADR_NAME_SUFFIX`, repository variable of the same name for CI) appended to the end, for example `kv-adr-dev-scus-01b`. One setting solves a collision; no hash is built into any name.
5. **Abbreviations.** Taken from the published list and pinned by a unit test. Two differ from the examples in the request and from the Azure Developer CLI templates: the page gives `cosno` for a Cosmos DB for NoSQL account (`cosmos` is its abbreviation for a Cosmos DB database) and `asp` for an App Service plan (not `plan`). Changing either is one line in `infra/abbreviations.json` plus the matching line in the unit test.
6. **Tags** on the resource group and every resource that takes tags:

   | Tag | Value | Why |
   |---|---|---|
   | `workload` | `adr` (`workloadName`) | the same word as in the names |
   | `application` | `adr-mcp` | the application the workload serves; the key the other estate uses |
   | `environment` | `dev` (`environmentShort`) | the same word as in the names; the key the other estate uses |
   | `component` | `api`, `data`, `security`, `monitoring` or `network` | which part of the application the resource belongs to; not on the resource group, which holds all of them |
   | `azd-env-name` | `adr-dev` (`environmentName`) | azd needs it to find the environment's resources |

   The function app also keeps `azd-service-name: api`, which `azd deploy` needs. The `workload` value changes from `adr-mcp` to `adr`; `adr-mcp` is now the `application`.
7. **Region codes** are in `infra/region-codes.json`, loaded by Bicep. A region that is not in the file stops the deployment with a message that says so (a code is never guessed):

   | Region | Code | Region | Code |
   |---|---|---|---|
   | `australiaeast` | `aue` | `uksouth` | `uks` |
   | `canadacentral` | `cac` | `ukwest` | `ukw` |
   | `centralus` | `cus` | `westcentralus` | `wcus` |
   | `eastus` | `eus` | `westeurope` | `weu` |
   | `eastus2` | `eus2` | `westus` | `wus` |
   | `francecentral` | `frc` | `westus2` | `wus2` |
   | `northcentralus` | `ncus` | `westus3` | `wus3` |
   | `northeurope` | `neu` | `southcentralus` | `scus` |

   All 16 support Flex Consumption (Observed, 2026-10-26). To add a region: confirm the code in two public lists and make sure no code is longer than four characters, then add it to the file and to this table.
8. **Child names.** Subnets are `snet-adr-app-dev-scus-01` and `snet-adr-pep-dev-scus-01`. Private endpoints are `pep-adr-cosmos-…`, `pep-adr-kv-…`, `pep-adr-blob-…` and `pep-adr-queue-…`. Private DNS zones keep the `privatelink.*` names Azure requires. A private DNS zone link takes the name of the virtual network it links to, because Microsoft publishes no abbreviation for it. Names inside a resource (Cosmos database `adr`, blob containers, the queue, the Key Vault secret `principal-hash-salt`) are data names, not Azure resource names, and do not change.
9. **Enforcement.** Bicep stops the deployment before anything is created when the region has no code, when a name part is not lower-case letters and digits, or when the Key Vault name would exceed 24 characters (the tightest limit of the four). `ResourceNamingTests` evaluates the real template offline for every region with `bicep snapshot` and checks every name against the pattern and the Azure rules below. CI installs Bicep before the unit tests and fails if it is missing.

### Names and the Azure rules
The rule column is from [Naming rules and restrictions](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules). The Result column is the unit test for every region in the table above; the longest name in any region is shown.

| Resource | Old name (token is a hash) | New name | Chars (longest) | Limit | Characters allowed | Scope | Result |
|---|---|---|---|---|---|---|---|
| Resource group | `rg-adr-dev` | `rg-adr-dev-scus-01` | 18 | 1-90 | letters, digits, `_ ( ) - .`, not ending in `.` | subscription | pass |
| Function app | `func-adr-<token>` | `func-adr-dev-scus-01` | 20 | 2-60 (kept to 32, see below) | letters, digits, hyphen, not at the ends | **global** | pass |
| App Service plan | `plan-adr-<token>` | `asp-adr-dev-scus-01` | 19 | 1-60 | letters, digits, hyphen | resource group | pass |
| Cosmos DB account | `cosmos-adr-<token>` | `cosno-adr-dev-scus-01` | 21 | 3-44 | lower-case letters, digits, hyphen; start with a letter or digit | **global** | pass |
| Key Vault | `kv-adr-<token>` | `kv-adr-dev-scus-01` | 18 | 3-24 | letters, digits, hyphen; start with a letter, end with a letter or digit; no `--` | **global** | pass |
| Storage account | `stadr<token>` | `stadrdevscus01` | 14 | 3-24 | lower-case letters and digits only | **global** | pass |
| Log Analytics workspace | `log-adr-<token>` | `log-adr-dev-scus-01` | 19 | 4-63 | letters, digits, hyphen; start and end with a letter or digit | resource group | pass |
| Application Insights | `appi-adr-<token>` | `appi-adr-dev-scus-01` | 20 | 1-260 | no `% & \ ? /`; not ending in space or `.` | resource group | pass |
| Managed identity | `id-adr-<token>` | `id-adr-dev-scus-01` | 18 | 3-128 | letters, digits, hyphen, underscore; start with a letter or digit | resource group | pass |
| Virtual network (private networking only) | `vnet-adr-<token>` | `vnet-adr-dev-scus-01` | 20 | 2-64 | letters, digits, `_ . -`; start with a letter or digit, end with a letter, digit or `_` | resource group | pass |
| Subnets (private networking only) | `app`, `private-endpoints` | `snet-adr-app-dev-scus-01`, `snet-adr-pep-dev-scus-01` | 24 | 1-80 | same as a virtual network | virtual network | pass |
| Private endpoints (private networking only) | `pe-<resource name>` | `pep-adr-cosmos-dev-scus-01`, `pep-adr-kv-…`, `pep-adr-blob-…`, `pep-adr-queue-…` | 26 | 2-64 | same as a virtual network | resource group | pass |
| Private DNS zone link (private networking only) | `<private endpoint>-link` | `vnet-adr-dev-scus-01` | 20 | 1-80 | same as a subnet | private DNS zone | pass |

Length budget: with the default parts and a region code of four characters (the longest), the Key Vault name has 18 characters, so a suffix or longer parts can add up to 6 characters before the 24 limit stops the deployment. The function app name is kept to 32 characters because Azure Functions shortens the name to 32 characters when it builds the host ID, which can make two apps that share a storage account collide ([Naming rules and restrictions](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules), Microsoft.Web note). With these abbreviations the storage account (24) and function app (32) names fit whenever the vault name fits, and the unit test checks all three.

## Rationale
- A name that can be read tells a junior administrator what a resource is, where it runs and which environment it belongs to. A hash tells nothing: from `func-adr-<token>` you cannot tell what a resource is, where it runs or which environment it belongs to without recomputing `uniqueString`.
- Putting region and instance in the name matches Microsoft's guidance and the owner's other Azure estate (for example `func-<app>-prod-cus-01` and `rg-<app>-sandbox-wus3-01`).
- Deterministic names make a documented host name (`func-adr-dev-scus-01.azurewebsites.net`) possible; with the hash, the docs could only say `<app>`.
- A suffix parameter handles the one real risk of readable names (a taken global name) in a single, visible setting.
- Pinning the abbreviations and checking names with the real template stops drift: a typo or a new resource type without a rule fails a test, not a deployment.

## Considered alternatives
1. **Keep the hash token** — rejected: unreadable, and a collision was never visible in the name.
2. **`plan-` and `cosmos-` as the Azure Developer CLI templates use** — rejected: they differ from Microsoft's published list, which the request named as the standard.
3. **Derive the environment word from the azd environment name** (`adr-dev` gives `dev`) — rejected: it ties naming to a string that also names the GitHub environment and the OIDC subject, which must not change (the request).
4. **Spell the region out** (`southcentralus`) — rejected: it uses about ten more characters of the 24 a Key Vault and a storage account allow.
5. **The Azure Naming Tool's region codes** (`ussc`, `use2`) — rejected: the owner's other estate already uses `cus` and `wus3`.
6. **An instance-number parameter** — rejected for now: nothing needs a second copy of the same workload in the same region; the suffix covers collisions.
7. **Add network security groups so every `nsg-` name in the request exists** — rejected: the template has none, and adding one to the delegated subnet is a security change, not a naming change. `nsg` is not defined until one exists.
8. **Secure unique default host names** (the app gets `<name>-<hash>.<region>.azurewebsites.net`; set with `autoGeneratedDomainNameLabelScope` on the site) — not adopted, left as an open decision for the owner. Microsoft [recommends](https://learn.microsoft.com/azure/app-service/reference-dangling-subdomain-prevention) it for every new app because nobody outside the tenant can then register the same host name. It would make the host name carry a hash, so the docs could no longer print it, and it is **Not verified** that Flex Consumption (FC1) accepts the setting. Without it the host name `func-adr-dev-scus-01.azurewebsites.net` is predictable and public: anyone could register it before the first deployment (the deployment would then fail, and `ADR_NAME_SUFFIX` is the way out). After a delete, App Service keeps the name for the owning tenant for a while, so the time to move clients and the Application ID URI is right after the delete. Say so if you want it changed.

## Consequences
- **Names cannot be changed in place** (Microsoft's own guidance says so). The existing `rg-adr-dev` and everything in it must be replaced: when the old group is deleted, the Cosmos DB data, the evidence blobs and the generated principal-hash salt are lost, and the new salt means old principal hashes would not match (nothing is kept, so nothing is compared). The steps are in [Replacing the dev environment after the naming change](../05-local-dev-and-deploy.md#replacing-the-dev-environment-after-the-naming-change).
- **The host name changes** from `func-adr-<token>.azurewebsites.net` to `func-adr-dev-scus-01.azurewebsites.net` (the second is **Not verified** until it is deployed). Every client setting that holds the old host must change: the `ADR_MCP_URL` environment variable behind `.mcp.json`, the host typed into `.vscode/mcp.json`, a registered Claude Code server, a Claude.ai or Desktop custom connector, and, in `EntraBuiltIn` mode, the Application ID URI of the server's Entra app registration (the MCP URL must equal it, [ADR-0008](ADR-0008-entra-oauth-sign-in-for-mcp-clients.md)).
- **The old Key Vault cannot be purged.** Purge protection holds a deleted vault for 90 days and nobody can shorten it ([Key Vault recovery](https://learn.microsoft.com/azure/key-vault/general/key-vault-recovery#what-are-soft-delete-and-purge-protection)). Its name stays reserved; the new vault has a different name, so nothing is blocked. The same applies later: deleting the environment and creating it again within 90 days finds `kv-adr-dev-scus-01` still reserved. Set `ADR_NAME_SUFFIX` (for example `b`) for the second run; that changes the four global names only. The same reservation makes the old template undeployable for 90 days after the delete unless the old vault is recovered first (see Rollback).
- **Merging deploys.** A change under `infra/` makes the `ci-cd` workflow ask for approval of a deployment to `adr-dev` (repository variable `ADR_DEPLOY_ENABLED` is `true`). The runbook approves it straight away, so the new environment is built next to the old one, checked, and only then is the old one deleted: Azure shows whether the new global names are free and whether tenant policy accepts them only at deployment time.
- **Tags change.** `workload` is now `adr` (it was `adr-mcp`) and `application`, `environment` and `component` are new. A cost report, alert or policy that filters on `workload = adr-mcp` needs the new value (**Not verified**: none is known in this repository).
- **The unit tests need the Bicep CLI.** They skip without it on a developer machine and fail in CI when it is missing (`ADR_TEST_REQUIRE_BICEP=true`). A run takes about 20 seconds more.
- **Not covered by the convention:** the names of Entra app registrations (`ADR MCP server`, `adr-mcp-deploy-adr-dev`), the GitHub environment `adr-dev` and the OIDC credential `github-adr-dev` (the request keeps them), and the action group `Application Insights Smart Detection` that Azure creates inside the group.
- Another subscription, region or environment is one parameter each: `location`, `environmentShort` and `ADR_ENVIRONMENT_SHORT`. When a second azd environment is added to the workflow, set `ADR_ENVIRONMENT_SHORT` for it, because the default is `dev`.

## Rollback
- **Before the old group is deleted** (parts A to C of the runbook): delete the new group `rg-adr-dev-scus-01` with `az group delete`, put back the Application ID URIs noted in the runbook, and point clients back. Nothing else was touched.
- **After the delete** there is no undo for the data. The old Key Vault can be recovered for 90 days (`az keyvault recover`), which brings back the vault and its secret, not the data. Because the old vault name is reserved for 90 days, **recover it first** if you revert this pull request and deploy the old template within that time; otherwise the old template fails with a "vault already exists" error. After 90 days the name is free again.
- Do not use `azd down` while both groups exist: it deletes the group recorded by the most recent deployment (the new one after this change, the old-named one after a revert and a deploy), which may not be the one you want gone. Delete the unwanted group by name with `az group delete`.

## Update 2026-10-28: a second workload, `adrmcp` (ADR-0010 phase 2)
The Obsidian vault MCP server ([ADR-0010](ADR-0010-obsidian-second-brain.md)) is deployed by a second entry template, `infra/vault/main.bicep`, from its own azd project (`src/VaultMcp/azure.yaml`, azd environment `vault-dev`) into its own resource group. The convention applies unchanged. What is new:
- **Workload** `adrmcp`, so `rg-adrmcp-dev-scus-01`; the tag `application` is `vault-mcp` (the application the workload serves; `adr-mcp` stays on the ADR server's resources). The `component` values are the same five.
- **Abbreviations** added from Microsoft's list: `ca` (container app), `cae` (Container Apps environment), `cr` (container registry). A registry name allows letters and digits only, so it has no hyphens: `cradrmcpdevscus01`.
- **Global names** in this template: the container registry, the Key Vault and the storage account take `nameSuffix` (`VAULT_NAME_SUFFIX`). The container app's host name is `<app name>.<environment's unique domain>.<region>.azurecontainerapps.io`, so the app name is unique per environment, not across Azure, and takes no suffix.
- **Parameters:** `VAULT_WORKLOAD_NAME` (default `adrmcp`), `VAULT_ENVIRONMENT_SHORT` (default `dev`), `VAULT_NAME_SUFFIX`. The azd environment name must end with `-<environmentShort>` (`vault-dev`), as for the ADR server, and the same stop-before-creating checks apply. In addition the workload names are kept disjoint: the vault template accepts only names that start with `adrmcp` (`adrmcp`, `adrmcp2`, ...), and the ADR template refuses every workload whose name joined with its environment begins with `adrmcp` (so `adrmcp`, `adrmcp2`, and also `adr` with environment `mcpde`): the storage account and registry names join workload and environment without a separator, so `adr` + `mcpde` would spell the same `stadrmcpdescus01` as `adrmcp` + `de`. The ADR server's own default, `adr` + `dev`, is unaffected. The same workload, environment and region would otherwise give the same resource group and global names, and one deployment would change or delete the other's resources; with the rule that cannot happen for any values.
- **Data names** (decision 8): the file shares are `vault` and `syncstate`, the environment's storage definitions `vault` (read-only), `vault-rw` and `syncstate`; the diagnostic setting is `log-analytics`; the Key Vault secrets are `obsidian-vault-password`, `obsidian-email` and `obsidian-password`.
- `ResourceNamingTests` evaluates this template too, for every region, with the rules below, and checks that the two workloads share no name in one subscription and region. "Build every name in the entry template" now means `infra/main.bicep` or `infra/vault/main.bicep`; modules still build none.

| Resource | Name | Chars | Limit | Characters allowed | Scope | Result |
|---|---|---|---|---|---|---|
| Resource group | `rg-adrmcp-dev-scus-01` | 21 | 1-90 | as above | subscription | pass |
| Container app | `ca-adrmcp-dev-scus-01` | 21 | 2-32 | lower-case letters, digits, hyphens; start with a letter, end with a letter or digit | resource group | pass |
| Container app, sync client (step 2, component `sync`) | `ca-adrmcp-sync-dev-scus-01` | 26 | 2-32 | as the container app | resource group | pass |
| Container Apps environment | `cae-adrmcp-dev-scus-01` | 22 | not listed on the Learn page (read 2026-10-28); the test applies the container app rule | lower-case letters, digits, hyphens; start with a letter, end with a letter or digit | resource group | pass |
| Container registry | `cradrmcpdevscus01` | 17 | 5-50 | letters and digits only | **global** | pass |
| Key Vault | `kv-adrmcp-dev-scus-01` | 21 | 3-24 | as above | **global** | pass |
| Storage account | `stadrmcpdevscus01` | 17 | 3-24 | lower-case letters and digits only | **global** | pass |
| Log Analytics workspace | `log-adrmcp-dev-scus-01` | 22 | 4-63 | as above | resource group | pass |
| Managed identity | `id-adrmcp-dev-scus-01` | 21 | 3-128 | as above | resource group | pass |

Length budget: the Key Vault name is again the tightest. With a 4-character region code, `kv-adrmcp-dev-wcus-01` has 21 characters, so 3 remain for a longer workload, environment or suffix; `kv-adrmcpx-prod-wcus-01b` (24) is the longest the test proves. The template stops before creating anything when the budget is exceeded, as for the ADR server.
