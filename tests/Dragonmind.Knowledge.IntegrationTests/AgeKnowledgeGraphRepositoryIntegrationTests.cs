using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="ApacheAgeKnowledgeGraphRepository"/> against a real
/// PostgreSQL + Apache AGE instance: round trips, scope isolation (including the case where two
/// scopes name the very same entity), minimum-distance traversal without duplicate facts, Cypher
/// injection rejection, and Unix-seconds timestamp round-tripping.
/// </summary>
[Collection("Postgres")]
public sealed class AgeKnowledgeGraphRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<FactId> _createdFactIds = new();
    private readonly List<string> _createdEntityNames = new();
    private IKnowledgeGraphRepository _repository = null!;
    private IDbContextFactory<KnowledgeDbContext> _contextFactory = null!;

    public AgeKnowledgeGraphRepositoryIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync()
    {
        _contextFactory = _fixture.ContextFactory;
        _repository = new ApacheAgeKnowledgeGraphRepository(
            _contextFactory,
            NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Deletes every fact this test created, then proves the vertices are actually gone by
    /// re-querying the graph directly — the same belt-and-suspenders shape
    /// <see cref="TrackedDocumentCleanup"/> uses for documents.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var factId in _createdFactIds)
        {
            await _repository.DeleteAsync(factId);
        }

        if (_createdEntityNames.Count == 0)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
                await setup.ExecuteNonQueryAsync();
            }

            var nameList = string.Join(", ", _createdEntityNames.Select(n => $"'{n}'"));

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $@"
                    SELECT * FROM cypher('{KnowledgeGraph.Name}', $$
                        MATCH (n:Entity) WHERE n.name IN [{nameList}]
                        DETACH DELETE n
                    $$) as (result agtype);
                ";
                await delete.ExecuteNonQueryAsync();
            }

            // Belt-and-suspenders: prove the vertices are actually gone rather than assuming the
            // DELETE above succeeded silently.
            await using var verify = connection.CreateCommand();
            verify.CommandText = $@"
                SELECT * FROM cypher('{KnowledgeGraph.Name}', $$
                    MATCH (n:Entity) WHERE n.name IN [{nameList}]
                    RETURN n.name
                $$) as (name agtype);
            ";
            await using var reader = await verify.ExecuteReaderAsync();
            var remaining = await reader.ReadAsync();
            if (remaining)
            {
                throw new InvalidOperationException(
                    "Knowledge graph test cleanup left at least one tracked vertex behind.");
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Builds an entity name that is unique to this test run and contains only letters and digits,
    /// which keeps it comfortably inside <c>SanitizeCypher</c>'s character allowlist without needing
    /// to reason about escaping.
    /// </summary>
    private string CreateUniqueEntityName(string baseName)
    {
        var name = baseName + Guid.NewGuid().ToString("N");
        _createdEntityNames.Add(name);
        return name;
    }

    private async Task<KnowledgeFact> AddFactAsync(ScopeId scopeId, string subject, string predicate, string @object)
    {
        var fact = KnowledgeFact.Create(
            scopeId,
            GraphEntity.Create(subject, "Entity"),
            predicate,
            GraphEntity.Create(@object, "Entity"));

        await _repository.AddAsync(fact);
        _createdFactIds.Add(fact.Id);
        return fact;
    }

    [Fact]
    public async Task AddAsync_GetByIdAsync_RoundTrips()
    {
        var scopeId = ScopeId.New();
        var subject = CreateUniqueEntityName("Orders");
        var @object = CreateUniqueEntityName("Payments");

        var fact = await AddFactAsync(scopeId, subject, "DEPENDS_ON", @object);

        var retrieved = await _repository.GetByIdAsync(fact.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(fact.Id.Value, retrieved.Id.Value);
        Assert.Equal(scopeId, retrieved.ScopeId);
        Assert.Equal(subject, retrieved.Subject.Name);
        Assert.Equal(@object, retrieved.Object.Name);
    }

    [Fact]
    public async Task GetByScopeIdAsync_ReturnsFactsForThatScope()
    {
        var scopeId = ScopeId.New();
        var subject = CreateUniqueEntityName("Checkout");
        var placeA = CreateUniqueEntityName("Cart");
        var placeB = CreateUniqueEntityName("Billing");

        var factA = await AddFactAsync(scopeId, subject, "DEPENDS_ON", placeA);
        var factB = await AddFactAsync(scopeId, subject, "DEPENDS_ON", placeB);

        var results = await _repository.GetByScopeIdAsync(scopeId);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, f => f.Id.Value == factA.Id.Value);
        Assert.Contains(results, f => f.Id.Value == factB.Id.Value);
    }

    [Fact]
    public async Task GetFactsWithinDistanceAsync_MinimumDistance_NoDuplicateFactsAtDepthTwo()
    {
        // Triangle X-Y, X-Z, Y-Z. From X at depth 2, the undirected variable-length traversal
        // reaches edge X-Y via path X-Y (length 1) AND via path X-Z-Y (length 2); it reaches X-Z
        // the same way; it reaches Y-Z only via a length-2 path. Without the `min(length(path))`
        // aggregation, X-Y and X-Z would each appear TWICE (once at distance 1, once at distance 2)
        // — this is exactly the duplicate-fact defect the repository's aggregation comment
        // describes. Each fact must appear exactly once, at its true shortest distance.
        var scopeId = ScopeId.New();
        var x = CreateUniqueEntityName("Xray");
        var y = CreateUniqueEntityName("Yankee");
        var z = CreateUniqueEntityName("Zulu");

        var factXY = await AddFactAsync(scopeId, x, "KNOWS", y);
        var factXZ = await AddFactAsync(scopeId, x, "KNOWS", z);
        var factYZ = await AddFactAsync(scopeId, y, "KNOWS", z);

        var results = await _repository.GetFactsWithinDistanceAsync(x, scopeId, maxDistance: 2);

        Assert.Equal(3, results.Count);
        AssertNoDuplicateFacts(results);

        Assert.Equal(1, results.Single(r => r.Fact.Id.Value == factXY.Id.Value).Distance);
        Assert.Equal(1, results.Single(r => r.Fact.Id.Value == factXZ.Id.Value).Distance);
        Assert.Equal(2, results.Single(r => r.Fact.Id.Value == factYZ.Id.Value).Distance);
    }

    private static void AssertNoDuplicateFacts(IReadOnlyList<(KnowledgeFact Fact, int Distance)> results)
    {
        var distinctFactIds = results.Select(r => r.Fact.Id.Value).Distinct().Count();
        Assert.Equal(results.Count, distinctFactIds);
    }

    [Fact]
    public async Task TimestampsRoundTripAsUnixSeconds()
    {
        var scopeId = ScopeId.New();
        var subject = CreateUniqueEntityName("Inventory");
        var @object = CreateUniqueEntityName("Warehouse");

        var fact = await AddFactAsync(scopeId, subject, "PART_OF", @object);

        // AddAsync stores DateTimeOffset(fact.Timestamp).ToUnixTimeSeconds(), so anything finer
        // than whole seconds is lost on the way in — the round trip is only exact once the
        // original value is itself truncated to seconds.
        var expected = DateTimeOffset.FromUnixTimeSeconds(
            new DateTimeOffset(fact.Timestamp).ToUnixTimeSeconds()).UtcDateTime;

        var retrieved = await _repository.GetByIdAsync(fact.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(expected, retrieved.Timestamp);
    }

    [Theory]
    [InlineData("Orders'; DETACH DELETE n RETURN '")]
    [InlineData("Entity{injection}")]
    [InlineData("Service MATCH")]
    public async Task GetFactsBySubjectAsync_InjectionPayload_RejectedBeforeAnySqlRuns(string maliciousInput)
    {
        var scopeId = ScopeId.New();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.GetFactsBySubjectAsync(maliciousInput, scopeId));

        // If sanitization ran after opening a connection instead of before, this payload would
        // have mutated the graph. Proving the scope stayed empty is the "no SQL ran" evidence.
        var scoped = await _repository.GetByScopeIdAsync(scopeId);
        Assert.Empty(scoped);
    }

    [Fact]
    public async Task AddAsync_UnknownRelationshipType_RejectedBeforeAnySqlRuns()
    {
        // Relationship type labels are interpolated into Cypher unquoted (AGE cannot parameterize
        // them), so the allowlist check is the only thing standing between an arbitrary label and
        // the query text. It must run before any connection is used.
        var scopeId = ScopeId.New();
        var subject = CreateUniqueEntityName("Alpha");
        var @object = CreateUniqueEntityName("Bravo");
        var fact = KnowledgeFact.Create(
            scopeId,
            GraphEntity.Create(subject, "Entity"),
            "DROP DATABASE",
            GraphEntity.Create(@object, "Entity"));

        await Assert.ThrowsAsync<ArgumentException>(() => _repository.AddAsync(fact));

        var scoped = await _repository.GetByScopeIdAsync(scopeId);
        Assert.Empty(scoped);
    }

    /// <summary>
    /// The two-scope isolation contract: scope A holds X-LOCATED_IN-&gt;Y, scope B holds
    /// Y-LOCATED_IN-&gt;Z. Entity <c>Y</c> is the literal same vertex in both facts (AGE's MERGE
    /// matches vertices by name), so this is the case the plain "different sessions never collide"
    /// tests can't reach: the SAME entity name is shared across two scopes, and every read method
    /// — not just the depth-2 traversal — must still keep them apart.
    /// </summary>
    [Fact]
    public async Task TwoScopesSharingAnEntityName_NoReadMethodLeaksAcrossScopes()
    {
        var scopeA = ScopeId.New();
        var scopeB = ScopeId.New();
        var x = CreateUniqueEntityName("Frontend");
        var y = CreateUniqueEntityName("Gateway");
        var z = CreateUniqueEntityName("Backend");

        var factXY = await AddFactAsync(scopeA, x, "LOCATED_IN", y); // scope A: X -> Y
        var factYZ = await AddFactAsync(scopeB, y, "LOCATED_IN", z); // scope B: Y -> Z (shares Y)

        // GetFactsBySubjectAsync: Y is a subject only in scope B.
        Assert.Empty(await _repository.GetFactsBySubjectAsync(y, scopeA));
        var subjectB = Assert.Single(await _repository.GetFactsBySubjectAsync(y, scopeB));
        Assert.Equal(factYZ.Id.Value, subjectB.Id.Value);

        // GetFactsByObjectAsync: Y is an object only in scope A.
        var objectA = Assert.Single(await _repository.GetFactsByObjectAsync(y, scopeA));
        Assert.Equal(factXY.Id.Value, objectA.Id.Value);
        Assert.Empty(await _repository.GetFactsByObjectAsync(y, scopeB));

        // GetFactsByEntityAsync: each scope sees only its own edge touching Y.
        var entityA = Assert.Single(await _repository.GetFactsByEntityAsync(y, scopeA));
        Assert.Equal(factXY.Id.Value, entityA.Id.Value);
        var entityB = Assert.Single(await _repository.GetFactsByEntityAsync(y, scopeB));
        Assert.Equal(factYZ.Id.Value, entityB.Id.Value);

        // GetFactsWithinDistanceAsync: a depth-2 traversal from X structurally reaches the scope B
        // edge (Y->Z) at distance 2 unless every edge in the path is constrained to scope A — the
        // defect this whole test class exists to prove is fixed.
        var fromX = await _repository.GetFactsWithinDistanceAsync(x, scopeA, maxDistance: 2);
        var onlyFromX = Assert.Single(fromX);
        Assert.Equal(factXY.Id.Value, onlyFromX.Fact.Id.Value);
        Assert.Equal(1, onlyFromX.Distance);
        Assert.DoesNotContain(fromX, r => r.Fact.Id.Value == factYZ.Id.Value);
        Assert.DoesNotContain(fromX, r => r.Fact.Object.Name == z);

        // Mirror from scope B's side over the same graph shape.
        var fromY = await _repository.GetFactsWithinDistanceAsync(y, scopeB, maxDistance: 2);
        var onlyFromY = Assert.Single(fromY);
        Assert.Equal(factYZ.Id.Value, onlyFromY.Fact.Id.Value);
        Assert.DoesNotContain(fromY, r => r.Fact.Id.Value == factXY.Id.Value);
    }
}
