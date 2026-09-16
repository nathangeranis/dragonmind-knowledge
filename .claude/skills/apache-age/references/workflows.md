# Apache AGE Workflows Reference

## Contents
- Graph Extension Setup
- Knowledge Context Registration
- Adding a New Relationship Type
- Debugging Graph Queries
- Testing Graph Repositories

---

## Graph Extension Setup

The `age` extension and the `knowledge_graph` graph are created by the `InitialCreate` EF Core
migration — this repository ships no separate init script for it. The migration's `Up()` runs, in
order:

```sql
CREATE EXTENSION IF NOT EXISTS age;   -- suppressTransaction: true

SELECT ag_catalog.create_graph('knowledge_graph')
WHERE NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph');
```

Both statements are safe to run twice — applying migrations a second time is a no-op (see
`MigrationTests`). Load AGE into a session before any `cypher()` call:

```sql
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
```

### Verify AGE Is Working

```sql
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$
  RETURN 1 AS test
$$) AS (test agtype);
-- Should return: 1
```

---

## Knowledge Context Registration

`ApacheAgeKnowledgeGraphRepository` takes `IDbContextFactory<KnowledgeDbContext>` plus an
`ILogger` — it creates a context per operation and drops to the raw `DbConnection` for Cypher. See
the **entity-framework** skill for how the EF Core `KnowledgeDbContext` coexists with raw-connection
Cypher access.

```csharp
// Repository constructor (src/Dragonmind.Knowledge/Infrastructure/Persistence/Repositories/)
public ApacheAgeKnowledgeGraphRepository(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ILogger<ApacheAgeKnowledgeGraphRepository> logger)
```

The repository and `IGraphTraversalService`/`GraphTraversalService` are registered by
`AddKnowledgeContext` (handlers are NOT registered individually — MediatR assembly scanning
discovers them).

---

## Adding a New Relationship Type

Copy this checklist and track progress:

- [ ] Step 1: Add the predicate to the `Allowed` set in `RelationshipTypes`
      (`src/Dragonmind.Knowledge/Domain/ValueObjects/RelationshipTypes.cs`). **Do not add an
      allowlist anywhere else** — the repository and `CreateKnowledgeFactCommandHandler` both derive
      from this one set.
- [ ] Step 2: Update the literal pins in `RelationshipTypesTests` — they are deliberately
      hand-written so a dropped or mistyped entry fails the build.
- [ ] Step 3: Add a unit test in `Dragonmind.Knowledge.UnitTests` covering the new predicate through
      `CreateKnowledgeFactCommandHandler` (accepted), and a case for a spelling that should still be
      rejected.
- [ ] Step 4: Verify with a manual Cypher query against a database started via
      `docker compose up -d --wait db`.

Example: adding `CONTAINS`

```csharp
// Step 1: the single source of truth (Domain/ValueObjects/RelationshipTypes.cs)
public static readonly IReadOnlySet<string> Allowed = new HashSet<string>
{
    "IS_A", "PART_OF", "LOCATED_IN", "DEPENDS_ON", "OWNS", "CREATED_BY",
    "MEMBER_OF", "RELATED_TO", "REFERENCES", "CONTAINS", "REPLACES", "KNOWS",
};
```

```sql
-- Step 4: verify against the local compose database
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$
  MERGE (a:Entity {name: 'Checkout Service'})
  MERGE (b:Entity {name: 'Orders API'})
  MERGE (a)-[:DEPENDS_ON]->(b)
  RETURN a.name, b.name
$$) AS (a agtype, b agtype);
```

---

## Debugging Graph Queries

### Inspect All Nodes

```sql
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$
  MATCH (n:Entity)
  RETURN n.name AS name
  ORDER BY n.name
  LIMIT 100
$$) AS (name agtype);
```

### Inspect All Edges

```sql
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$
  MATCH (a)-[r]->(b)
  RETURN a.name AS from, type(r) AS rel, b.name AS to
  LIMIT 100
$$) AS (from agtype, rel agtype, to agtype);
```

### Timestamps Are Unix Seconds, Not ISO Dates

`AddAsync` stores `r.timestamp` as Unix epoch **seconds**, not a formatted date, specifically to
avoid escaping issues with an ISO-8601 string inside the Cypher literal. That has one consequence
worth remembering: a date-substring probe against the raw property text can never match, even when
the write landed cleanly —

```sql
-- WRONG: always returns 0, whether or not the write happened
SELECT count(*) FROM knowledge_graph."DEPENDS_ON" WHERE properties::text LIKE '%2026-01%';
```

Decode the timestamp instead, or probe by an exact id:

```sql
SELECT (SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'knowledge_graph' AND c.oid = e.tableoid)              AS label,
       to_timestamp(((e.properties::text)::json->>'timestamp')::bigint) AT TIME ZONE 'UTC' AS written_utc
FROM knowledge_graph._ag_label_edge e
WHERE (e.properties::text)::json->>'scope_id' = '<scope-id>'
  AND (e.properties::text)::json->>'timestamp' ~ '^[0-9]+$'   -- keep the ::bigint cast total
ORDER BY written_utc;
```

`_ag_label_edge` is AGE's parent table for every edge label, so it counts edges without needing to
enumerate which label tables exist.

### Check for Duplicate Nodes (Should Be Zero)

```sql
SELECT * FROM ag_catalog.cypher('knowledge_graph', $$
  MATCH (n:Entity)
  WITH n.name AS name, count(n) AS cnt
  WHERE cnt > 1
  RETURN name, cnt
$$) AS (name agtype, cnt agtype);
```

If duplicates exist, you have a `CREATE` instead of `MERGE` bug. See `references/patterns.md`.

### Common Error Messages

| Error | Cause | Fix |
|-------|-------|-----|
| `function cypher(...) does not exist` | `search_path` not set | Run `SET search_path = ag_catalog, ...` first |
| `graph "knowledge_graph" does not exist` | Migration not applied | Run `dotnet ef database update` (or restart `docker compose up -d --wait db` and re-migrate) |
| `agtype input function not found` | AGE not loaded | Run `LOAD 'age'` on the connection |
| `column "..." of relation "..." does not exist` | Wrong AS clause column count | Match `AS (col1 agtype, col2 agtype)` to the RETURN columns |

---

## Testing Graph Repositories

Coverage is two-layer:

- **Unit** (`tests/Dragonmind.Knowledge.UnitTests`): `CypherInjectionTests` and
  `CypherInjectionReadMethodTests` verify the injection defense. `SanitizeCypher` and the
  `RelationshipTypes.Allowed` set reject malicious input *before* any connection is opened, so these
  tests assert `ArgumentException` and use strict mocks where `IDbContextFactory<KnowledgeDbContext>`
  is never invoked:

  ```csharp
  var factoryMock = new Mock<IDbContextFactory<KnowledgeDbContext>>(MockBehavior.Strict);
  var repo = new ApacheAgeKnowledgeGraphRepository(factoryMock.Object, loggerMock.Object);

  await Assert.ThrowsAsync<ArgumentException>(
      () => repo.GetFactsBySubjectAsync("x') RETURN 1 UNION MATCH (n) DETACH DELETE n //", scopeId));
  // Strict mock: the test fails if the repository ever touched the factory
  ```

- **Integration** (`tests/Dragonmind.Knowledge.IntegrationTests`, real Postgres via
  `docker compose up -d --wait db`): `AgeKnowledgeGraphRepositoryIntegrationTests` covers the
  add/get round trip, scope isolation between two scope ids (including when they name the same
  entity), minimum-distance traversal at depth 1 and 2 with no duplicate facts, injection rejected
  before any SQL runs, and that timestamps round-trip as Unix seconds. There is no path-finding
  method (`shortestPath()` isn't supported on this AGE version), so there's no `FindPath` test either.

When adding a new Cypher-backed repository method, add a matching injection test that feeds hostile
input through every string parameter, and an integration test that proves scope isolation for it.

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests
docker compose up -d --wait db && dotnet test tests/Dragonmind.Knowledge.IntegrationTests
```

See the **xunit** and **moq** skills for test structure. See the **docker** skill for the compose setup.
