# Use System.Text.Json; prohibit Newtonsoft.Json in new services

- Status: Accepted
- Tags: serialization, dotnet, standards

## Context
Mixed serializers caused inconsistent casing and duplicate converters across services. System.Text.Json is built into .NET 8 and meets our performance needs.

## Decision
All services must use System.Text.Json. We must not use Newtonsoft.Json in any new or migrated service. Existing usages are removed during the next maintenance window.

## Rationale
One serializer simplifies contracts, reduces dependency surface and removes a transitive package with a past DoS CVE.

## Considered Options
- Keep both serializers with a shared converter package
- Standardize on Newtonsoft.Json
- Standardize on System.Text.Json (chosen)

## Consequences
Teams need to port custom converters. Rollback: re-add the package behind an adapter. Monitoring: dependency drift check via adr_detect_drift in CI.
