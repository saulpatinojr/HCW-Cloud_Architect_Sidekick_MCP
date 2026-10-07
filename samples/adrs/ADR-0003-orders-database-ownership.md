# OrderService exclusively owns the OrdersDb database

- Status: Accepted
- Tags: data, ownership, bounded-context

## Context
Multiple services wrote directly to OrdersDb, causing schema coupling and failed deployments in the Orders bounded context.

## Decision
OrderService owns the OrdersDb database. Other services must integrate through the Orders API or published events; direct database access is not allowed.

## Rationale
Database-per-service restores autonomy and lets the Orders team evolve the schema independently.

## Considered Options
- Shared database with views
- Database-per-service (chosen)

## Consequences
Billing needs a read model fed by events. Rollback: temporary read-only replica access. Monitoring: database-ownership drift detector against configuration evidence.
