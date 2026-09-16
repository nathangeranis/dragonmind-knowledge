# EF Core Migration Workflows

## Contents
- Migration Checklist
- Adding a New Migration
- Applying Migrations
- How Tests Apply Migrations
- Rolling Back
- Common Migration Errors

---

## Migration Checklist

Copy this checklist when adding a new property or schema change:

- [ ] Step 1: Update the domain aggregate and its value objects
- [ ] Step 2: Add/update `IEntityTypeConfiguration<T>` in `Infrastructure/Persistence/EntityConfigurations/`
- [ ] Step 3: Confirm the `DbSet<T>` is on `KnowledgeDbContext`
- [ ] Step 4: `dotnet build Dragonmind.Knowledge.sln` — confirm no compile errors
- [ ] Step 5: Create the migration (see below)
- [ ] Step 6: Review the generated migration file — check snake_case, column types, indexes
- [ ] Step 7: Apply it to the local compose database
- [ ] Step 8: Run `dotnet test tests/Dragonmind.Knowledge.UnitTests` and, with `docker compose up -d --wait db`, `dotnet test tests/Dragonmind.Knowledge.IntegrationTests`

---

## Adding a New Migration

```bash
export KNOWLEDGE_DB_CONNECTION="Host=127.0.0.1;Port=5455;Database=knowledge;Username=knowledge"

dotnet ef migrations add AddDocumentContentIndex \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

**Design-time factory:** `KnowledgeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>`
reads `KNOWLEDGE_DB_CONNECTION` and throws if it's unset — there is no localhost fallback, so a
missing environment variable fails immediately with a clear message rather than silently targeting
the wrong database. This is why `dotnet ef migrations add` can resolve a connection without any
application host running.

---

## Applying Migrations

```bash
# Apply pending migrations to the local compose database
docker compose up -d --wait db
dotnet ef database update \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

# Apply to a different database by overriding the connection string
KNOWLEDGE_DB_CONNECTION="Host=127.0.0.1;Port=5499;Database=knowledge;Username=knowledge" \
dotnet ef database update \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

**Validate:** After applying, confirm with:
```bash
dotnet ef migrations list \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
# All migrations should show "(applied)"
```

---

## How Tests Apply Migrations

This repository ships no host application, so nothing calls `Database.MigrateAsync()` automatically
on startup. Two things apply migrations instead:

- **`dotnet ef database update`** — for local development, as above.
- **`PostgresFixture`** (`tests/Dragonmind.Knowledge.IntegrationTests`) — requires
  `KNOWLEDGE_TEST_CONNECTION` to be set (it fails fast rather than skipping tests when it's missing),
  builds the data source via `KnowledgeDataSource.Create`, and calls `Database.MigrateAsync()` once
  per test run before any test executes.

`docker compose --profile test up --build --exit-code-from tests` wires both: it starts `db`, waits
for its healthcheck, then runs the whole test suite in a container whose `KNOWLEDGE_TEST_CONNECTION`
points at it — the integration tests migrate the database themselves via `PostgresFixture`.

---

## Rolling Back Migrations

```bash
# Roll back to a specific migration by name
dotnet ef database update AddDocumentCreatedAtIndex \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

# Roll back ALL migrations (destructive!)
dotnet ef database update 0 \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

# Remove the last unapplied migration file
dotnet ef migrations remove \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

**Feedback loop:**
1. Roll back: `dotnet ef database update {PreviousMigration} --project ...`
2. Remove the file: `dotnet ef migrations remove --project ...`
3. Fix the entity configuration
4. Recreate: `dotnet ef migrations add {Name} --project ...`
5. Review the generated SQL: `dotnet ef migrations script --project ...`
6. If it looks correct, apply: `dotnet ef database update --project ...`

---

## Common Migration Errors

### "The migration has already been applied"

```bash
# Check applied migrations in the database
dotnet ef migrations list --project src/Dragonmind.Knowledge --startup-project src/Dragonmind.Knowledge --context KnowledgeDbContext
```
If the migration file was deleted but the database record still exists, prefer the rollback
approach above over hand-editing the history table.

### "Unable to create a 'KnowledgeDbContext'"

```bash
# Confirm the connection string is actually set
echo $KNOWLEDGE_DB_CONNECTION
```
`KnowledgeDbContextFactory` throws immediately if this is empty — there's no other cause for this
specific message in a two-project solution.

### snake_case not applied

If column names appear in PascalCase in the generated migration:
```csharp
// Confirm the EFCore.NamingConventions package is referenced and
// UseSnakeCaseNamingConvention() is actually called, in AddKnowledgeContext
options.UseNpgsql(dataSource, npgsql => { /* ... */ })
       .UseSnakeCaseNamingConvention();
```

### Value object not persisted correctly

```bash
# Generate the SQL preview before applying
dotnet ef migrations script \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext \
    --output migration_preview.sql

# Review migration_preview.sql to verify column types and names
```

See the **postgresql** skill for connection troubleshooting and the **ddd** skill for value object
patterns.
