# ADR 006 · PostgreSQL and .NET 10

**Status:** accepted · supersedes the persistence and target-framework decisions in ADR 001–005 and in the original specs.

## Context

The specs picked .NET 9 and SQLite in WAL mode. SQLite was chosen for one reason: zero infrastructure for whoever clones the repo, with Postgres left as "a connection string change, documented but not implemented".

Two things make that trade-off worse than it looked:

- **"Documented but not implemented" is where portfolio repos lose credibility.** The claim that swapping to Postgres is a one-line change is only true until you compare `ILIKE` with `LIKE`, `DateOnly` mapping, `decimal` precision, concurrent writers, and `timestamptz`. A repo that recommends Postgres should run the engine it documents.
- **The tests would have been testing the wrong engine.** The integration suite is the main evidence that the write gate holds. Running it on SQLite while deploying on Postgres means the provider translation — exactly the layer most likely to differ — is never exercised.

.NET 10 is the current release; there is no reason a repo written now targets the previous one.

## Decision

- Target **net10.0**.
- Persist to **PostgreSQL** via `Npgsql.EntityFrameworkCore.PostgreSQL`. No SQLite anywhere, including tests.
- Migrations and seed run on startup (`DatabaseStartup.MigrateAndSeedAsync`).
- Integration tests create and drop a real database per run, against a `postgres:16` service container in CI.

## Consequences

**What this costs.** Cloning the repo now needs a database. That is one command — `docker compose up -d postgres` — and it is in `.agent/commands.md` and the README quick start, but it is no longer literally zero infrastructure. The under-five-minutes goal survives; "no dependencies at all" does not.

**What this buys.**

- The tests exercise the engine that ships. `EF.Functions.ILike`, `DateOnly` → `date` and `decimal(18,2)` are all covered by the suite rather than assumed.
- Concurrent writers stop being a design constraint. SQLite in WAL mode was chosen partly to survive chat, audit and usage rows being written at once; with Postgres that concern disappears, along with the reasoning about it.
- Deployment is the same engine as development, so a VPS deploy is a connection string and nothing else.

**Accepting a client-generated key.** Entity identifiers are UUIDv7 created in the domain, and the model declares them `ValueGeneratedNever`. With a store-generated key, EF reads a pre-set identifier on a new entity as evidence the row already exists and issues an `UPDATE` that matches nothing. The domain-generated key lets inserts through a navigation collection work.

**Where the connection string comes from.** `POSTGRES_CONNECTION_STRING` or `DATABASE_URL`, accepting either the Npgsql keyword form or the `postgres://user:pass@host/db` URL that hosting panels hand out.
