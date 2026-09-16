# PostgreSQL Workflows Reference

## Contents
- Migration Workflow
- Local Docker Database Setup
- Verifying Extensions and the Graph
- Rollback Migration
- Troubleshooting Checklist

---

## Migration Workflow

This repository has one context and one DbContext, so there's no `--project`/`--context` table to
consult — it's always the same pair. The pattern is still: add migration, review the generated SQL,
apply it.

**Copy this checklist:**
- [ ] Set `KNOWLEDGE_DB_CONNECTION` to a reachable database (the local compose database works)
- [ ] Run `dotnet ef migrations add`
- [ ] Review the generated migration file in `src/Dragonmind.Knowledge/Migrations/`
- [ ] Run `dotnet ef database update`
- [ ] Verify the change in `psql`

```bash
export KNOWLEDGE_DB_CONNECTION="Host=127.0.0.1;Port=5455;Database=knowledge;Username=knowledge"

dotnet ef migrations add AddDocumentCreatedAtIndex \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

dotnet ef database update \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

`KnowledgeDbContextFactory` (the design-time factory `dotnet ef` resolves) reads
`KNOWLEDGE_DB_CONNECTION` only — it throws rather than falling back to a guessed local connection
string, so a forgotten environment variable fails loudly instead of silently targeting the wrong
database.

---

## Local Docker Database Setup

```bash
# Start Postgres (pgvector + Apache AGE image) only
docker compose up -d --wait db

# Verify it's healthy
docker compose ps

# Connect with psql
psql -h 127.0.0.1 -p 5455 -U knowledge -d knowledge

# Check logs if the healthcheck never turns green
docker compose logs db
```

The local database:
- Host: `127.0.0.1:5455` (override the port with `KNOWLEDGE_DB_PORT`)
- Database / user: `knowledge` / `knowledge`
- Auth: `trust`, bound to `127.0.0.1` — there is no password to set or leak locally
- Image: built from `docker/postgres/Dockerfile` (`apache/age:release_PG16_1.6.0` +
  `postgresql-16-pgvector`)

For the full one-command flow (build, migrate, run every test) see the **docker** skill.

---

## Verifying Extensions and the Graph

Both extensions and the graph are created by the `InitialCreate` migration — there is no manual
setup step for a fresh database. To confirm they're actually present after migrating:

```sql
SELECT extname FROM pg_extension WHERE extname IN ('vector', 'age');

SELECT * FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph';

-- pgvector sanity check
SELECT '[1,2,3]'::vector;  -- Should return (1,2,3) without error

-- Apache AGE sanity check
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$ RETURN 1 $$) AS (result agtype);
-- Should return: 1
```

`MigrationTests` (in `tests/Dragonmind.Knowledge.IntegrationTests`) automates exactly this check,
plus that a second `MigrateAsync` against an already-migrated database is a no-op.

---

## Rollback Migration

```bash
# Roll back to a specific migration (by name, not timestamp prefix)
dotnet ef database update PreviousMigrationName \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

# Then remove the unwanted migration file
# (delete src/Dragonmind.Knowledge/Migrations/*_UnwantedMigration.cs)

# Recreate migration with correct content
dotnet ef migrations add CorrectedMigrationName \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

**Do NOT** delete a migration file without first rolling back the database it was applied to. If it
was already applied somewhere you can't roll back, add a new corrective migration instead of editing
history.

---

## Troubleshooting Checklist

**Connection refused:**
1. Check the container is up and healthy: `docker compose ps`
2. Start it if it isn't: `docker compose up -d --wait db`
3. Confirm the port matches what you set `KNOWLEDGE_DB_CONNECTION` / `KNOWLEDGE_TEST_CONNECTION` to — the default is `5455`, overridable via `KNOWLEDGE_DB_PORT`

**Migration fails: `type "vector" does not exist` or `extension "age" does not exist`:**
- You're targeting a database that never ran `InitialCreate`. Run migrations from an empty database, not from an arbitrary later state.

**Migration fails: `Unable to create a 'KnowledgeDbContext'`:**
- Confirm `KNOWLEDGE_DB_CONNECTION` is set — `KnowledgeDbContextFactory` throws immediately if it isn't, so this is almost always a missing environment variable, not a code problem.

**Migration already applied error:**
1. Roll back: `dotnet ef database update PreviousMigrationName --project ...`
2. Delete the migration `.cs` file
3. Recreate: `dotnet ef migrations add NewName --project ...`

**Apache AGE queries return errors:**
- Always set `search_path` before Cypher queries:
  ```sql
  SET search_path = ag_catalog, "$user", public;
  ```
- Verify the graph exists: `SELECT * FROM ag_catalog.ag_graph;`

See the **entity-framework** skill for DbContext-specific configuration patterns, and the
**apache-age** skill for everything Cypher-related.
