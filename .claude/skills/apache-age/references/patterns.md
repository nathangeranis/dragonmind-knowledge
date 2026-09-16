# Apache AGE Patterns Reference

## Contents
- Connection Setup
- Node and Relationship Creation
- Graph Traversal Queries
- Result Deserialization
- Anti-Patterns
- Security: Cypher Injection

---

## Connection Setup

AGE requires `ag_catalog` on the PostgreSQL `search_path` before any Cypher executes. NEVER assume this is set globally — set it per-connection.

```csharp
// ApacheAgeKnowledgeGraphRepository.SetupAgeAsync — runs before every query
await using var cmd = connection.CreateCommand();
cmd.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
await cmd.ExecuteNonQueryAsync(cancellationToken);
```

If you skip this, you get `function cypher(...) does not exist` — not an obvious error message.

---

## Node and Relationship Creation

### MERGE (Always Use This)

AGE's `cypher()` takes a *literal string body* — Npgsql parameter binding does not work inside it. The repository sanitizes inputs (`SanitizeCypher`) and interpolates them directly:

```csharp
// Values sanitized via SanitizeCypher BEFORE interpolation (throws on bad input)
var query = $@"
    SELECT * FROM cypher('knowledge_graph', $$
        MERGE (a:Entity {{name: '{subject}', type: '{subjectType}'}})
        MERGE (b:Entity {{name: '{obj}', type: '{objType}'}})
        MERGE (a)-[r:{predicate} {{fact_id: '{sanitizedFactId}', scope_id: '{sanitizedScopeId}', timestamp: '{unixTimestamp}'}}]->(b)
        RETURN a
    $$) as (result agtype);
";

await using var cmd = connection.CreateCommand();
cmd.CommandText = query;
await cmd.ExecuteNonQueryAsync(ct);
```

**Why MERGE, not CREATE:** `CREATE` on a duplicate entity creates a second disconnected node. Anything that stores the same subject repeatedly — reprocessing a document, retrying a write path — produces duplicate nodes within minutes if it uses `CREATE`. `MERGE` is idempotent.

### Dynamic Predicates

AGE does NOT support parameterized relationship types — you must interpolate the predicate string. This means predicates MUST be validated against an allowlist before interpolation, and before the repository is ever called:

```csharp
// SAFE - allowlist validation happens in the command handler, before the repository is invoked.
// The allowlist lives in ONE place, RelationshipTypes.Allowed (Domain/ValueObjects/) — never declare a second one.
var normalizedPredicate = RelationshipTypes.Normalize(fact.Predicate);
if (!RelationshipTypes.IsAllowed(normalizedPredicate))
{
    return null; // the handler writes nothing and logs once
}
// normalizedPredicate is now safe to interpolate as a bare Cypher label.
// The repository re-checks the same allowlist as defense in depth before it ever builds SQL.
```

---

## Graph Traversal Queries

### Bounded Depth Traversal, Scoped

```csharp
var query = $"""
    SELECT DISTINCT entity::text
    FROM ag_catalog.cypher('{KnowledgeGraph.Name}', $$
      MATCH path = (start:Entity {{name: '{SanitizeCypher(entityName)}'}})
            -[*1..{maxDepth} {{scope_id: '{sanitizedScope}'}}]-
            (related:Entity)
      RETURN DISTINCT related.name AS entity
      LIMIT 50
    $$) AS (entity agtype);
    """;
```

Always bound the depth (`*1..N`), and include `LIMIT` on exploratory traversals. Without them, a
highly-connected entity can trigger a full graph scan.

### Scope Is a Property, Not an Afterthought Filter

Every fact-bearing edge carries a `scope_id` property at write time (see *Node and Relationship
Creation*). A read that does not filter on it returns facts belonging to a different caller — this
is the single most important isolation rule in this repository:

```sql
MATCH (start:Entity {name: 'Checkout Service'})-[r {scope_id: '<scope>'}]->(related)
RETURN related
```

The same property map works on a variable-length edge (`-[*1..2 {scope_id: '<scope>'}]-`), and AGE
applies it to every edge in the path, so a multi-hop traversal cannot cross into another scope
through a shared `Entity` vertex. Avoid `WHERE all(r IN relationships(path) ...)` directly on the
`MATCH` (apache/age issue 2516, `XX000 no relation entry for relid`); see
[SKILL.md](../SKILL.md) → *Scope-Constrained Traversal*.

---

## Result Deserialization

Every AGE result column is `agtype`, an opaque wire type. Cast each one to `::text` in the outer
`SELECT` — not inside the Cypher body — so the raw ADO.NET reader can read it as a string without
registering any extra AGE type-mapping plugin. That cast already unwraps a scalar string (no
surrounding quotes, escapes resolved) and renders an agtype null as SQL NULL, so no separate C#
unwrapping step is needed — `ReadFactRow` just reads each ordinal as text-or-null into a `FactRow`,
and `AgeFactRowMapper.TryMap` turns that row into a `KnowledgeFact`:

```csharp
// ApacheAgeKnowledgeGraphRepository.ReadFactRow — ordinals match FactColumnsAsText
private static FactRow ReadFactRow(DbDataReader reader) => new(
    FactId: ReadText(reader, 0), Subject: ReadText(reader, 1), SubjectType: ReadText(reader, 2),
    Predicate: ReadText(reader, 3), Object: ReadText(reader, 4), ObjectType: ReadText(reader, 5),
    ScopeId: ReadText(reader, 6), Timestamp: ReadText(reader, 7));

private static string? ReadText(DbDataReader reader, int ordinal)
    => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

// AgeFactRowMapper.TryMap — returns Mapped / SkippedIncomplete / SkippedMalformed rather than throwing,
// because most of the stored graph predates fact metadata (missing fact_id/type on older edges).
// A mapped row's predicate has its stored underscores turned back into spaces, its timestamp parsed
// from Unix seconds, and is rebuilt via Reconstitute — WITHOUT raising domain events, since this
// represents an existing fact, not a new one:
var unixSeconds = long.Parse(row.Timestamp!, CultureInfo.InvariantCulture);
var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
fact = KnowledgeFact.Reconstitute(
    FactId.From(Guid.Parse(row.FactId!)), ScopeId.From(Guid.Parse(row.ScopeId!)),
    GraphEntity.Create(row.Subject!, subjectType), row.Predicate!.Replace("_", " ", StringComparison.Ordinal),
    GraphEntity.Create(row.Object!, objectType), timestamp);
```

---

## Anti-Patterns

### WARNING: String Concatenation Without Sanitization

**The Problem:**

```csharp
// BAD - direct external input in Cypher
var query = $"MATCH (n:Entity {{name: '{externalInput}'}}) RETURN n";
```

**Why This Breaks:**
1. Cypher injection: input `'}) RETURN 1 UNION MATCH (n) DETACH DELETE n //` deletes the entire graph
2. AGE does not support parameterized Cypher values the same way SQL supports `$1` for string literals
3. The graph holds accumulated knowledge — corruption is silent and there is no migration that undoes it

**The Fix:**

The real `SanitizeCypher` (in `ApacheAgeKnowledgeGraphRepository`) **rejects** bad input rather than stripping it — three ordered steps:

```csharp
// GOOD - reject-don't-strip, before any DB connection is opened
// Step 1: character allowlist regex ^[a-zA-Z0-9 '_\-.,;:()]+$  (throws ArgumentException on mismatch)
// Step 2: token-based Cypher keyword rejection (split on ' ', '_', '-', '.'; reject MATCH/DELETE/MERGE/...)
// Step 3: escape single quotes ('  ->  \')  — backslash and double-quote never reach here (Step 1 rejects them)
var sanitized = SanitizeCypher(entityName);
```

### WARNING: Opening a New Graph Connection Per Query

**The Problem:**

```csharp
// BAD - new connection + search_path setup for every fact stored
foreach (var fact in facts)
{
    await using var conn = await _dataSource.OpenConnectionAsync();
    await SetSearchPath(conn);
    await StoreFact(conn, fact);
}
```

**Why This Breaks:**
1. Connection pool exhaustion under any batch write
2. Each `search_path` SET is a round-trip to PostgreSQL
3. Transaction boundaries are lost between iterations

**The Fix:**

```csharp
// GOOD - one connection, one transaction, batch the facts
await using var conn = await _dataSource.OpenConnectionAsync(ct);
await SetSearchPath(conn, ct);
await using var tx = await conn.BeginTransactionAsync(ct);
foreach (var fact in facts)
{
    await StoreFactWithConnection(conn, fact, ct);
}
await tx.CommitAsync(ct);
```

### WARNING: Missing DISTINCT on Traversal Returns

Without `DISTINCT`, a bidirectional relationship between A and B at depth 2 returns A→B, B→A, A→B→A, B→A→B — quadratic explosion. Always use `RETURN DISTINCT`.

---

## Security: Cypher Injection

See the allowlist pattern for predicates above. For entity names and ids, `SanitizeCypher` applies:
1. Character-allowlist regex rejection (throws on any disallowed character)
2. Token-based Cypher keyword rejection (throws on MATCH, DELETE, MERGE, etc.)
3. Single-quote escaping for string-literal embedding
4. NEVER reflect raw external input directly into Cypher — even input that already passed a caller's own validation is sanitized again here

See the **csharp** skill for input validation patterns. See the **dotnet** skill for Npgsql configuration.
