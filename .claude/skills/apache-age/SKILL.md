---
name: apache-age
description: |
  Manages Apache AGE graph queries and entity relationships in the Knowledge bounded context.
  Use when: writing or debugging Cypher queries in ApacheAgeKnowledgeGraphRepository, adding new
  graph traversal patterns, storing KnowledgeFact entities/relationships, configuring the AGE graph,
  or troubleshooting graph query results.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Apache AGE Skill

Apache AGE is the graph-database layer of the Knowledge bounded context (`Dragonmind.Knowledge`),
storing and traversing entity-relationship triples (subject/predicate/object) as `KnowledgeFact`
aggregates. All graph operations go through `ApacheAgeKnowledgeGraphRepository`
(`src/Dragonmind.Knowledge/Infrastructure/Persistence/Repositories/`), surfaced to callers outside
this repository only through `IKnowledgeContextFacade`. Cypher queries are embedded in SQL and
execute against PostgreSQL 16 with the AGE extension loaded.

**The single most important constraint**: AGE's `cypher()` function takes a *literal string body* —
normal Npgsql parameter binding cannot be used inside it. The repository therefore hand-rolls
injection defense (`SanitizeCypher` + the `RelationshipTypes.Allowed` set) and interpolates
sanitized values directly. Never write `$parameter`-style Cypher here; it does not work.

## Contents
- Quick Start
- Key Concepts
- Common Patterns
- See Also
- Related Skills

## Quick Start

### Store a Relationship

```csharp
// Via IKnowledgeContextFacade (the only way callers outside Dragonmind.Knowledge reach the graph)
var created = await _knowledge.AddKnowledgeFactAsync(
    subject: "Checkout Service",
    predicate: "DEPENDS_ON",       // must be in RelationshipTypes.Allowed
    @object: "Payments DB",
    scopeId: scopeId);
```

### Traverse the Graph

```csharp
// Facade: find related facts within maxDepth hops, scoped to one caller
var facts = await _knowledge.GetRelatedFactsAsync(entityName: "Checkout Service", scopeId, maxDepth: 2);

// Inside the Knowledge context: IGraphTraversalService returns (Fact, Distance) tuples
var results = await _graphTraversalService.GetRelatedFactsAsync(entityName, scopeId, maxDepth, ct);
```

### Raw Cypher (inside ApacheAgeKnowledgeGraphRepository)

Inputs are sanitized first, then interpolated — no parameter binding. Every returned `agtype`
column is cast to `::text` in the outer `SELECT`, not inside the Cypher body, so the result can be
read back as plain text without an extra AGE type-mapping plugin:

```csharp
var sanitizedName = SanitizeCypher(entityName);   // throws on disallowed chars/keywords
var sanitizedScope = SanitizeCypher(scopeId.Value.ToString());
var query = $@"
    SELECT fact_id::text, subject::text, subject_type::text, predicate::text,
           obj::text, obj_type::text, scope_id::text, timestamp::text
    FROM cypher('knowledge_graph', $$
        MATCH (a)-[r {{scope_id: '{sanitizedScope}'}}]->(b)
        WHERE a.name = '{sanitizedName}'
        RETURN r.fact_id AS fact_id, a.name AS subject, a.type AS subject_type, type(r) AS predicate,
               b.name AS obj, b.type AS obj_type, r.scope_id AS scope_id, r.timestamp AS timestamp
    $$) as (fact_id agtype, subject agtype, subject_type agtype, predicate agtype, obj agtype, obj_type agtype, scope_id agtype, timestamp agtype);
";
```

## Key Concepts

| Concept | Usage | Notes |
|---------|-------|-------|
| Graph name | `knowledge_graph` | Created once by the `InitialCreate` EF Core migration (`SELECT ag_catalog.create_graph(...)`, guarded by an existence check) — not recreated by later migrations |
| Node label | `Entity` | All knowledge nodes; properties `name` and `type` |
| Relationship | Uppercase predicate string | e.g. `DEPENDS_ON`, `PART_OF`; must pass the `RelationshipTypes.Allowed` set (single source of truth — relationship type labels are interpolated unquoted and cannot be parameterized) |
| `SanitizeCypher` | Guards every string literal | Character allowlist regex → token-based Cypher-keyword rejection → single-quote escaping; throws `ArgumentException` on failure, *before* opening a connection |
| `agtype` → text | Cast in the outer `SELECT`, not inside the Cypher body | `fact_id::text` etc.; the cast already unwraps a scalar string (no surrounding quotes, escapes resolved) and renders an agtype null as SQL NULL — no separate unwrap step needed in C# |
| `SetupAgeAsync` | Per-connection setup | Runs `LOAD 'age'; SET search_path = ag_catalog, "$user", public;` before every query |
| `MERGE` vs `CREATE` | MERGE prevents duplicate nodes | `AddAsync` MERGEs both entities and the relationship — always MERGE, never CREATE |
| `Reconstitute` | Rebuild aggregates from query rows | `AgeFactRowMapper.TryMap(...)` calls `KnowledgeFact.Reconstitute(...)`, not `Create()` (no domain events on rehydration) |
| Scope | Every read that resolves an entity by NAME carries a `scope_id` | See *Scope-Constrained Traversal* below — a name-based query that reads one edge without the scope predicate is a bug. `GetByIdAsync` is exempt by design: a `FactId` is globally unique, so it identifies one fact outright instead of searching a namespace two scopes can both occupy, and `GetByScopeIdAsync` carries the scope in its own argument. Neither is an oversight to "fix" |
| Testing | Unit tests plus a real-database integration suite | `CypherInjectionTests`/`CypherInjectionReadMethodTests` (unit, mocked) verify the sanitizer; `AgeKnowledgeGraphRepositoryIntegrationTests` (integration, real Postgres via compose) verify traversal and scope isolation |

## Common Patterns

### MERGE Entity Nodes (Idempotent Writes)

**When:** Storing a `KnowledgeFact` where the entities may already exist (`AddAsync`). The predicate
is allowlist-validated by the caller — see *Relationship Allowlist* below — before the repository is
invoked at all; the repository re-validates as defense in depth. All string values are sanitized;
the timestamp is stored as Unix seconds to avoid escaping issues with a formatted date string —
which also means a `properties::text LIKE '%2026-01%'`-style substring probe never matches, and
misreads as "nothing was written" even when the write landed. Decode the timestamp instead of
pattern-matching it (see [workflows](references/workflows.md)):

```csharp
var query = $@"
    SELECT * FROM cypher('knowledge_graph', $$
        MERGE (a:Entity {{name: '{subject}', type: '{subjectType}'}})
        MERGE (b:Entity {{name: '{obj}', type: '{objType}'}})
        MERGE (a)-[r:{predicate} {{
            fact_id: '{sanitizedFactId}',
            scope_id: '{sanitizedScopeId}',
            timestamp: '{unixTimestamp}'
        }}]->(b)
        RETURN a
    $$) as (result agtype);
";
```

### Relationship Allowlist

```csharp
// Validated in CreateKnowledgeFactCommandHandler, before the repository is ever invoked.
// The allowlist lives in ONE place, RelationshipTypes.Allowed (Domain/ValueObjects/) —
// never declare a second one.
var normalizedPredicate = RelationshipTypes.Normalize(fact.Predicate);
if (!RelationshipTypes.IsAllowed(normalizedPredicate))
{
    // Write nothing; the handler returns null and logs once.
    return null;
}
// normalizedPredicate is now safe to interpolate as a bare Cypher label.
// The repository re-checks the same allowlist as defense in depth before it ever builds SQL.
```

### Scope-Constrained Traversal

**When:** Any traversal (`GetFactsBySubjectAsync`, `GetFactsByObjectAsync`, `GetFactsByEntityAsync`,
`GetFactsWithinDistanceAsync`) must return only facts that belong to the caller's scope — not the
whole graph. `Entity` vertices are merged by name and shared across scopes, so filtering only the
anchor node is not enough: every edge a traversal walks must carry the caller's scope, or a
multi-hop path can reach another scope's facts through a shared vertex.

Put the scope in a property map on the relationship pattern. On Apache AGE 1.6 this works for
variable-length edges too, and AGE applies the map to **every** edge in the path:

```csharp
var query = $@"
    SELECT {FactColumnsAsText}, distance::text
    FROM cypher('{KnowledgeGraph.Name}', $$
        MATCH path = (src:Entity)-[*1..{maxDistance} {{scope_id: '{sanitizedScope}'}}]-(tgt:Entity)
        WHERE src.name = '{sanitizedName}'
        UNWIND relationships(path) AS rel
        WITH rel, min(length(path)) AS distance
        RETURN {FactCypherProjectionFromRelationship}, distance
    $$) as ({FactColumnTypes}, distance agtype);
";
```

Two traps to keep out of this query:
- **Duplicate distances.** An undirected variable-length pattern reaches the same edge once per
  path. `RETURN DISTINCT ..., length(path)` keeps one row per (edge, distance) pair; group with
  `WITH rel, min(length(path)) AS distance` so each fact appears once at its shortest distance.
- **List predicates over a path.** Do not write `WHERE all(r IN relationships(path) WHERE ...)`
  directly on the `MATCH` — AGE (apache/age issue 2516) raises `XX000 no relation entry for relid`
  at plan time for list predicates over `relationships(path)`/`nodes(path)`. If you ever need one,
  project first: `WITH path, relationships(path) AS rels WHERE all(r IN rels WHERE ...)`.

`shortestPath()` is not supported by this AGE version (`syntax error at or near shortestPath`), so
there is no path-finding method; use a bounded variable-length `MATCH` if one is ever needed.

## See Also

- [patterns](references/patterns.md) — Cypher patterns, anti-patterns, Npgsql integration
- [workflows](references/workflows.md) — Graph setup, adding a relationship type, debugging steps

## Related Skills

- See the **postgresql** skill for AGE extension setup and PostgreSQL configuration
- See the **pgvector** skill for the semantic-search half of the Knowledge context
- See the **cqrs** and **ddd** skills for the query handlers and facade that sit above `IGraphTraversalService`
- See the **csharp** skill for `agtype` parsing patterns in C#
