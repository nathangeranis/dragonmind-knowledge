using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.ValueObjects;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository for knowledge graph using Apache AGE graph database.
/// Uses KnowledgeDbContext for database connections.
///
/// Note: read queries deliberately never bring an <c>agtype</c> value across the wire. Npgsql
/// binds unprepared statements requesting the binary result format, and the ApacheAGE agtype
/// converter supports text only, so reading agtype directly fails with "Resolved converter does
/// not support Binary format." Every projection therefore casts to text inside PostgreSQL — see
/// <see cref="FactColumnsAsText"/>. Writes are unaffected because they read no rows.
/// </summary>
public sealed class ApacheAgeKnowledgeGraphRepository : IKnowledgeGraphRepository
{
    private readonly IDbContextFactory<KnowledgeDbContext> _contextFactory;
    private readonly ILogger<ApacheAgeKnowledgeGraphRepository> _logger;

    /// <summary>
    /// Cypher keywords that must never appear as tokens in entity names or other Cypher string-literal inputs.
    /// Stored as a static readonly field to avoid re-allocation on every sanitization call.
    /// </summary>
    private static readonly HashSet<string> SuspiciousKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "MATCH", "WHERE", "DELETE", "DROP", "CREATE", "MERGE",
        "SET", "REMOVE", "DETACH", "RETURN", "OPTIONAL", "UNION",
        "WITH", "UNWIND", "CALL", "YIELD"
    };

    /// <summary>
    /// Precompiled character-allowlist regex used by <see cref="SanitizeCypher"/>.
    /// Cached as a static readonly field to avoid repeated regex parsing and allocation on every call.
    /// Only characters valid in entity names (letters, digits, space, apostrophe, underscore, hyphen, dot)
    /// are permitted; backslash and double-quote are intentionally excluded.
    /// Commas, colons, semicolons, and parentheses are allowed for natural entity names
    /// (e.g., "Checkout Service, v2" or "ACME Corp. (vendor)").
    /// </summary>
    private static readonly Regex AllowedCharactersRegex =
        new(@"^[a-zA-Z0-9 '_\-.,;:()]+$", RegexOptions.Compiled);

    /// <summary>
    /// Outer SELECT list shared by every query that maps rows onto <see cref="KnowledgeFact"/>.
    /// </summary>
    /// <remarks>
    /// Each column is cast to text inside PostgreSQL rather than being read as an
    /// <c>agtype</c>. Npgsql binds unprepared statements requesting the binary result format,
    /// while the ApacheAGE agtype converter supports text only, so reading an agtype column
    /// directly throws "Resolved converter does not support Binary format." and every graph
    /// read fails. AGE's agtype-to-text cast unwraps string values (no surrounding quotes,
    /// escapes already resolved) and renders an agtype null as SQL NULL, which is precisely
    /// what <see cref="AgeFactRowMapper"/> expects.
    ///
    /// Ordinals here are load-bearing: they must stay in step with <see cref="ReadFactRow"/>.
    /// </remarks>
    internal const string FactColumnsAsText = @"
                    fact_id::text,
                    subject::text,
                    subject_type::text,
                    predicate::text,
                    obj::text,
                    obj_type::text,
                    scope_id::text,
                    timestamp::text";

    /// <summary>
    /// Column type list for the <c>cypher()</c> call. AGE requires these to be declared as
    /// <c>agtype</c>; the cast to text happens in the outer SELECT.
    /// </summary>
    internal const string FactColumnTypes =
        "fact_id agtype, subject agtype, subject_type agtype, predicate agtype, " +
        "obj agtype, obj_type agtype, scope_id agtype, timestamp agtype";

    /// <summary>
    /// Cypher projection for the <c>(a)-[r]-&gt;(b)</c> pattern, ordered to match
    /// <see cref="FactColumnsAsText"/>.
    /// </summary>
    internal const string FactCypherProjection = @"
                        r.fact_id AS fact_id, a.name AS subject, a.type AS subject_type, type(r) AS predicate,
                        b.name AS obj, b.type AS obj_type, r.scope_id AS scope_id, r.timestamp AS timestamp";

    /// <summary>
    /// Cypher projection for traversal queries, which bind the relationship as <c>rel</c> and reach
    /// their endpoints through <c>startNode</c>/<c>endNode</c>. Ordered to match
    /// <see cref="FactColumnsAsText"/>.
    /// </summary>
    internal const string FactCypherProjectionFromRelationship = @"
                        rel.fact_id AS fact_id,
                        startNode(rel).name AS subject, startNode(rel).type AS subject_type,
                        type(rel) AS predicate,
                        endNode(rel).name AS obj, endNode(rel).type AS obj_type,
                        rel.scope_id AS scope_id, rel.timestamp AS timestamp";

    /// <summary>
    /// Collapses each relationship in a bounded traversal to a single row carrying its shortest
    /// hop count.
    /// </summary>
    /// <remarks>
    /// The traversal pattern is undirected and variable-length, so <c>UNWIND relationships(path)</c>
    /// yields the same edge once per path that reaches it. Returning <c>DISTINCT ..., length(path)</c>
    /// put the distance inside the DISTINCT key, so an edge reachable at two different path lengths
    /// survived as two rows with two different distances — measured against a graph with heavy
    /// fan-out, that inflated a depth-2 lookup by 20-50%, and the duplicate facts were rendered
    /// verbatim into the downstream prompt. Aggregating with <c>min</c> groups by the relationship
    /// and keeps the true shortest distance.
    /// </remarks>
    internal const string FactCypherDistanceAggregation = "WITH rel, min(length(path)) AS distance";

    public ApacheAgeKnowledgeGraphRepository(
        IDbContextFactory<KnowledgeDbContext> contextFactory,
        ILogger<ApacheAgeKnowledgeGraphRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<KnowledgeFact?> GetByIdAsync(
        FactId id,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedId = SanitizeCypher(id.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);
            var query = $@"
                SELECT {FactColumnsAsText}
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (a)-[r]->(b)
                    WHERE r.fact_id = '{sanitizedId}'
                    RETURN {FactCypherProjection}
                $$) as ({FactColumnTypes});
            ";

            var facts = await ExecuteQueryForFactsAsync(connection, query, cancellationToken);
            return facts.Count > 0 ? facts[0] : null;
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeFact>> GetByScopeIdAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedScopeId = SanitizeCypher(scopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);

            var query = $@"
                SELECT {FactColumnsAsText}
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (a)-[r]->(b)
                    WHERE r.scope_id = '{sanitizedScopeId}'
                    RETURN {FactCypherProjection}
                $$) as ({FactColumnTypes});
            ";

            return await ExecuteQueryForFactsAsync(connection, query, cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeFact>> GetFactsBySubjectAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedName = SanitizeCypher(entityName);
        var sanitizedScopeId = SanitizeCypher(scopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);
            var query = $@"
                SELECT {FactColumnsAsText}
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (a)-[r {{scope_id: '{sanitizedScopeId}'}}]->(b)
                    WHERE a.name = '{sanitizedName}'
                    RETURN {FactCypherProjection}
                $$) as ({FactColumnTypes});
            ";

            return await ExecuteQueryForFactsAsync(connection, query, cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeFact>> GetFactsByObjectAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedName = SanitizeCypher(entityName);
        var sanitizedScopeId = SanitizeCypher(scopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);
            var query = $@"
                SELECT {FactColumnsAsText}
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (a)-[r {{scope_id: '{sanitizedScopeId}'}}]->(b)
                    WHERE b.name = '{sanitizedName}'
                    RETURN {FactCypherProjection}
                $$) as ({FactColumnTypes});
            ";

            return await ExecuteQueryForFactsAsync(connection, query, cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeFact>> GetFactsByEntityAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedName = SanitizeCypher(entityName);
        var sanitizedScopeId = SanitizeCypher(scopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);
            var query = $@"
                SELECT {FactColumnsAsText}
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (a)-[r {{scope_id: '{sanitizedScopeId}'}}]->(b)
                    WHERE a.name = '{sanitizedName}' OR b.name = '{sanitizedName}'
                    RETURN {FactCypherProjection}
                $$) as ({FactColumnTypes});
            ";

            return await ExecuteQueryForFactsAsync(connection, query, cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task AddAsync(
        KnowledgeFact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact, nameof(fact));

        // Validate predicate against allowlist BEFORE opening any DB connection.
        // Relationship type labels go into Cypher unquoted and cannot be parameterized,
        // so only explicitly known types are accepted.
        var transformedPredicate = RelationshipTypes.Normalize(fact.Predicate);
        if (!RelationshipTypes.Allowed.Contains(transformedPredicate))
        {
            _logger.LogWarning("Rejected unknown predicate type {Predicate} for fact {FactId}", transformedPredicate, fact.Id);
            throw new ArgumentException(
                $"Unknown relationship type: '{fact.Predicate}'. Only known relationship types are allowed.",
                nameof(fact));
        }

        // Validate and sanitize entity names BEFORE opening any DB connection.
        // This provides eager feedback and prevents the mock factory from being hit in tests.
        var subject = SanitizeCypher(fact.Subject.Name);
        var subjectType = SanitizeCypher(fact.Subject.EntityType);
        var obj = SanitizeCypher(fact.Object.Name);
        var objType = SanitizeCypher(fact.Object.EntityType);
        // UUIDs only contain hex digits and dashes, which pass the allowlist; sanitize for consistency.
        var sanitizedFactId = SanitizeCypher(fact.Id.Value.ToString());
        var sanitizedScopeId = SanitizeCypher(fact.ScopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);

            // Predicate is allowlist-validated above; entity names are sanitized above.
            var predicate = transformedPredicate;

            // Use Unix timestamp (seconds since epoch) to avoid escaping issues with ISO 8601 format
            var unixTimestamp = new DateTimeOffset(fact.Timestamp).ToUnixTimeSeconds();

            var query = $@"
                SELECT * FROM cypher('{KnowledgeGraph.Name}', $$
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

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task UpdateAsync(
        KnowledgeFact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact, nameof(fact));

        // For graph databases, we delete and re-add to update
        await DeleteAsync(fact.Id, cancellationToken);
        await AddAsync(fact, cancellationToken);
    }

    public async Task DeleteAsync(
        FactId id,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedId = SanitizeCypher(id.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);
            var query = $@"
                SELECT * FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH ()-[r]->()
                    WHERE r.fact_id = '{sanitizedId}'
                    DELETE r
                $$) as (result agtype);
            ";

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<(KnowledgeFact Fact, int Distance)>> GetFactsWithinDistanceAsync(
        string entityName,
        ScopeId scopeId,
        int maxDistance = 2,
        CancellationToken cancellationToken = default)
    {
        // Validate input BEFORE opening any DB connection to fail fast on injection attempts.
        var sanitizedName = SanitizeCypher(entityName);
        var sanitizedScopeId = SanitizeCypher(scopeId.Value.ToString());

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();

        try
        {
            await EnsureConnectionOpenAsync(connection, cancellationToken);
            await SetupAgeAsync(connection, cancellationToken);

            // The scope goes on the EDGE pattern, not on the anchor node. AGE applies a property
            // map written on a variable-length edge to EVERY edge in the path, so a multi-hop
            // traversal cannot cross into another scope through a shared Entity vertex. Scoping
            // `src` instead would leave every hop past the first unscoped -- which is precisely the
            // shape the scope-isolation integration tests exist to catch.
            //
            // The list-predicate spelling of the same constraint --
            //   WHERE all(r IN relationships(path) WHERE r.scope_id = '...')
            // -- is not an option here: applied directly to the MATCH, this AGE build fails with
            // XX000 "no relation entry for relid" (apache/age issue 2516).
            var query = $@"
                SELECT {FactColumnsAsText},
                    distance::text
                FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH path = (src:Entity)-[*1..{maxDistance} {{scope_id: '{sanitizedScopeId}'}}]-(tgt:Entity)
                    WHERE src.name = '{sanitizedName}'
                    UNWIND relationships(path) as rel
                    {FactCypherDistanceAggregation}
                    RETURN {FactCypherProjectionFromRelationship},
                           distance
                $$) as ({FactColumnTypes}, distance agtype);
            ";

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;

            var results = new List<(KnowledgeFact, int)>();
            var skipped = new SkippedRowTally();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var outcome = AgeFactRowMapper.TryMap(ReadFactRow(reader), out var fact);
                if (outcome != FactRowMapOutcome.Mapped)
                {
                    skipped.Record(outcome);
                    continue;
                }

                // length(path) is an AGE integer; as text it is a plain base-10 literal.
                if (!int.TryParse(ReadText(reader, DistanceOrdinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance))
                {
                    skipped.Record(FactRowMapOutcome.SkippedMalformed);
                    continue;
                }

                results.Add((fact!, distance));
            }

            LogSkippedRows(skipped);
            return results;
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task EnsureConnectionOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }

    private async Task SetupAgeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Log the error but continue - graph may already exist or be configured
            _logger.LogDebug(ex, "AGE setup command failed (this is expected if graph already exists): {Message}", ex.Message);
        }
    }

    private async Task<IReadOnlyList<KnowledgeFact>> ExecuteQueryForFactsAsync(
        DbConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;

        var facts = new List<KnowledgeFact>();
        var skipped = new SkippedRowTally();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var outcome = AgeFactRowMapper.TryMap(ReadFactRow(reader), out var fact);
            if (outcome == FactRowMapOutcome.Mapped)
            {
                facts.Add(fact!);
            }
            else
            {
                skipped.Record(outcome);
            }
        }

        LogSkippedRows(skipped);
        return facts;
    }

    /// <summary>Ordinal of the trailing distance column in the traversal projection.</summary>
    private const int DistanceOrdinal = 8;

    /// <summary>
    /// Materialises the shared fact projection. Ordinals must match <see cref="FactColumnsAsText"/>.
    /// </summary>
    private static FactRow ReadFactRow(DbDataReader reader) => new(
        FactId: ReadText(reader, 0),
        Subject: ReadText(reader, 1),
        SubjectType: ReadText(reader, 2),
        Predicate: ReadText(reader, 3),
        Object: ReadText(reader, 4),
        ObjectType: ReadText(reader, 5),
        ScopeId: ReadText(reader, 6),
        Timestamp: ReadText(reader, 7));

    /// <summary>
    /// Reads a text column, mapping SQL NULL (an agtype null after the cast) onto a null string.
    /// </summary>
    private static string? ReadText(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reports rows the mapper rejected, once per query rather than once per row.
    /// </summary>
    /// <remarks>
    /// Most of the stored graph predates fact metadata — the majority of edges carry no
    /// <c>fact_id</c> and most vertices no <c>type</c> — so a per-row warning turned a single
    /// traversal into hundreds of log entries. Incomplete rows are expected history and stay at
    /// Debug; values that are present but unparseable are genuine corruption and still warn.
    /// </remarks>
    private void LogSkippedRows(SkippedRowTally skipped)
    {
        if (skipped.Incomplete > 0)
        {
            _logger.LogDebug(
                "Skipped {Count} knowledge graph rows missing a required field (fact_id, scope_id, timestamp, subject, predicate or object); most are edges stored before fact metadata was persisted.",
                skipped.Incomplete);
        }

        if (skipped.Malformed > 0)
        {
            _logger.LogWarning(
                "Skipped {Count} knowledge graph rows whose fact metadata could not be parsed.",
                skipped.Malformed);
        }
    }

    private struct SkippedRowTally
    {
        public int Incomplete { get; private set; }
        public int Malformed { get; private set; }

        public void Record(FactRowMapOutcome outcome)
        {
            if (outcome == FactRowMapOutcome.SkippedMalformed)
            {
                Malformed++;
            }
            else
            {
                Incomplete++;
            }
        }
    }

    /// <summary>
    /// Sanitizes string input for safe embedding in Cypher string literals.
    /// Uses a character allowlist as the primary gate, then token-based keyword detection
    /// to catch reserved words even when adjacent to delimiters like underscores.
    /// </summary>
    /// <remarks>
    /// Note: This method is for entity names and UUIDs used as string literal values in Cypher.
    /// Relationship type predicates must use <see cref="RelationshipTypes.Allowed"/> instead,
    /// because relationship type labels are interpolated unquoted and cannot be parameterized.
    /// </remarks>
    private string SanitizeCypher(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Input cannot be null or whitespace.", nameof(input));

        // Step 1: Character allowlist - only permit characters valid in entity names and UUIDs.
        // This is the primary security gate; anything outside this set is immediately rejected.
        // Note: backslash and double-quote are excluded from the allowlist, so they cannot
        // reach Step 3; only single-quote (') needs escaping below.
        if (!AllowedCharactersRegex.IsMatch(input))
        {
            _logger.LogWarning("Rejected input containing disallowed characters: {Input}", input);
            throw new ArgumentException("Input contains characters that are not allowed.", nameof(input));
        }

        // Step 2: Token-based keyword detection.
        // Split on common delimiters so that a predicate like "LOCATED_IN_MATCH" still
        // has its individual tokens inspected. This avoids the word-boundary false-negative
        // that occurs when keywords are adjacent to underscores (both are \w characters).
        var tokens = input.Split(new[] { ' ', '_', '-', '.' }, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (SuspiciousKeywords.Contains(token))
            {
                _logger.LogWarning("Rejected input containing Cypher keyword '{Keyword}': {Input}", token, input);
                throw new ArgumentException(
                    $"Input contains reserved keyword '{token}' and has been rejected for security reasons.",
                    nameof(input));
            }
        }

        // Step 3: Escape for Cypher string literals.
        // Only single-quote needs escaping; backslash and double-quote cannot reach this
        // point because they are rejected by the character allowlist in Step 1.
        return input.Replace("'", "\\'");
    }
}
