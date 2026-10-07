# All storage accounts must use private endpoints and TLS 1.2

- Status: Accepted
- Tags: network, security, storage

## Context
Public storage endpoints were flagged in the last assessment for the production environment.

## Decision
All storage accounts must have private endpoints and must use TLS 1.2 in production. Public network access is disabled.

## Rationale
Removes public exposure of blob/queue endpoints and enforces modern transport security.

## Considered Options
- Service endpoints only
- IP allow-lists
- Private endpoints (chosen)

## Consequences
Requires private DNS zones and VNet integration for consumers. Rollback: re-enable public access with firewall rules. Monitoring: Azure Policy compliance dashboard and drift scans.
