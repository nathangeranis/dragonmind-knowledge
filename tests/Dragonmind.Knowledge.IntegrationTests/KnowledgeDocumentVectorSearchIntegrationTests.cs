using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Integration tests for pgvector similarity search in <see cref="EfCoreKnowledgeDocumentRepository"/>.
/// Validates that the server-side cosine distance operator, HNSW index, and LIMIT/WHERE clauses work
/// correctly against real PostgreSQL + pgvector.
/// <para>
/// Uses hand-crafted 1536-dimensional vectors (not real embeddings) to test search mechanics, not
/// embedding quality.
/// </para>
/// </summary>
[Collection("Postgres")]
public sealed class KnowledgeDocumentVectorSearchIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<ScopeId> _testScopeIds = new();
    private EfCoreKnowledgeDocumentRepository _repository = null!;
    private IDbContextFactory<KnowledgeDbContext> _contextFactory = null!;

    public KnowledgeDocumentVectorSearchIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync()
    {
        _contextFactory = _fixture.ContextFactory;
        _repository = new EfCoreKnowledgeDocumentRepository(_contextFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Scoped by construction and verified by row count — see TrackedDocumentCleanup.
        await TrackedDocumentCleanup.DeleteTrackedDocumentsAsync(_contextFactory, _testScopeIds);
    }

    private ScopeId CreateTrackedScopeId()
    {
        var scopeId = ScopeId.New();
        _testScopeIds.Add(scopeId);
        return scopeId;
    }

    /// <summary>
    /// Creates a 1536-dim vector with alternating values for even/odd indices.
    /// </summary>
    private static float[] CreateVector(float evenValue, float oddValue)
    {
        var vector = new float[1536];
        for (int i = 0; i < 1536; i++)
            vector[i] = (i % 2 == 0) ? evenValue : oddValue;
        return vector;
    }

    /// <summary>
    /// Creates a uniform 1536-dim vector with the same value in every dimension.
    /// </summary>
    private static float[] CreateUniformVector(float value)
    {
        var vector = new float[1536];
        Array.Fill(vector, value);
        return vector;
    }

    private async Task<KnowledgeDocument> InsertDocumentWithVector(ScopeId scopeId, string content, float[] vector)
    {
        var doc = KnowledgeDocument.CreateWithEmbedding(
            scopeId,
            DocumentContent.Create(content, "Test"),
            Embedding.Create(vector));
        await _repository.AddAsync(doc);
        return doc;
    }

    [Fact]
    public async Task SearchBySimilarityAsync_IdenticalVectors_ReturnsSimilarityNearOne()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var vector = CreateUniformVector(1.0f);
        var inserted = await InsertDocumentWithVector(scopeId, "Identical vector test", vector);

        // Act — search with the same vector, using a very high minSimilarity so only the identical vector matches
        var results = await _repository.SearchBySimilarityAsync(vector, maxResults: 5, minSimilarity: 0.9999);

        // Assert — should return exactly the inserted document with very high similarity
        Assert.Single(results);
        var match = results[0];
        Assert.Equal(inserted.Id, match.Document.Id);
        Assert.True(match.Similarity > 0.99, $"Expected similarity > 0.99, got {match.Similarity}");
    }

    [Fact]
    public async Task SearchBySimilarityAsync_OrthogonalVectors_ReturnsLowerSimilarity()
    {
        // Arrange — insert two documents with different vectors
        var scopeId = CreateTrackedScopeId();
        var vectorA = CreateVector(1.0f, 0.0f);  // [1,0,1,0,...]
        var vectorB = CreateVector(0.0f, 1.0f);  // [0,1,0,1,...]
        await InsertDocumentWithVector(scopeId, "Vector A pattern", vectorA);
        await InsertDocumentWithVector(scopeId, "Vector B pattern", vectorB);

        // Act — search with vector A, scoped to this test's scope.
        // Scoping is load-bearing, not incidental: with minSimilarity 0.0 an unscoped search ranks
        // every other row in the table, and "Vector B pattern" scores exactly 0.0 against vectorA —
        // dead last. Against a populated database the top 10 fills with unrelated documents and the
        // First(...) below throws. The assertion is about A ranking above B, which scoping preserves.
        var results = await _repository.SearchBySimilarityAsync(
            vectorA, maxResults: 10, minSimilarity: 0.0, scopeId: scopeId);

        // Assert — vector A should be more similar to itself than to vector B
        Assert.True(results.Count >= 2, $"Expected at least 2 results, got {results.Count}");
        var similarityA = results.First(r => r.Document.Content.Value == "Vector A pattern").Similarity;
        var similarityB = results.First(r => r.Document.Content.Value == "Vector B pattern").Similarity;
        Assert.True(similarityA > similarityB,
            $"Expected A ({similarityA}) > B ({similarityB})");
    }

    [Fact]
    public async Task SearchBySimilarityAsync_RespectsMaxResults()
    {
        // Arrange — insert 5 documents
        var scopeId = CreateTrackedScopeId();
        var vector = CreateUniformVector(1.0f);
        for (int i = 0; i < 5; i++)
        {
            await InsertDocumentWithVector(scopeId, $"MaxResults test doc {i}", vector);
        }

        // Act — search with maxResults=2
        var results = await _repository.SearchBySimilarityAsync(vector, maxResults: 2);

        // Assert
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchBySimilarityAsync_RespectsMinSimilarity()
    {
        // Arrange — insert a similar and a dissimilar document
        var scopeId = CreateTrackedScopeId();
        var queryVector = CreateUniformVector(1.0f);
        var similarVector = CreateUniformVector(1.0f); // identical → similarity ~1.0
        var dissimilarVector = CreateVector(1.0f, -1.0f); // alternating → lower similarity

        await InsertDocumentWithVector(scopeId, "Similar doc", similarVector);
        await InsertDocumentWithVector(scopeId, "Dissimilar doc", dissimilarVector);

        // Act — search with high minSimilarity threshold
        var results = await _repository.SearchBySimilarityAsync(queryVector, maxResults: 10, minSimilarity: 0.95);

        // Assert — only the highly similar document should be returned
        var singleResult = Assert.Single(results);
        Assert.Equal("Similar doc", singleResult.Document.Content.Value);
        Assert.True(singleResult.Similarity >= 0.95,
            $"Expected similarity >= 0.95, got {singleResult.Similarity}");
    }

    [Fact]
    public async Task SearchBySimilarityAsync_OrderedBySimilarityDescending()
    {
        // Arrange — insert documents with vectors at varying distances
        var scopeId = CreateTrackedScopeId();
        var queryVector = CreateUniformVector(1.0f);

        // Closest: identical
        await InsertDocumentWithVector(scopeId, "Closest", CreateUniformVector(1.0f));
        // Middle: slightly different
        await InsertDocumentWithVector(scopeId, "Middle", CreateVector(1.0f, 0.5f));
        // Farthest: very different
        await InsertDocumentWithVector(scopeId, "Farthest", CreateVector(1.0f, -0.5f));

        // Act
        var results = await _repository.SearchBySimilarityAsync(queryVector, maxResults: 10, minSimilarity: 0.0);

        // Assert — we expect all three inserted documents to be returned
        Assert.True(results.Count >= 3, $"Expected at least 3 results, but got {results.Count}.");

        // The closest document should be the first result
        Assert.Equal("Closest", results[0].Document.Content.Value);

        // Assert — results should be ordered by similarity descending
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Similarity >= results[i].Similarity,
                $"Results not ordered: [{i - 1}]={results[i - 1].Similarity} < [{i}]={results[i].Similarity}");
        }
    }

    [Fact]
    public async Task SearchBySimilarityAsync_WithAScope_ExcludesOtherScopesDocuments()
    {
        // The whole point of the scope predicate: results are ranked purely by cosine distance, so
        // without it another scope's documents compete for the same LIMIT slots. Measured on a shared
        // dev database before the filter existed, 4 of 5 nearest neighbours for one scope's own probe
        // row belonged to a DIFFERENT scope.
        var mine = CreateTrackedScopeId();
        var theirs = CreateTrackedScopeId();
        var vector = CreateUniformVector(0.5f);

        var mineDoc = await InsertDocumentWithVector(mine, "My scope: the deployment finished", vector);
        await InsertDocumentWithVector(theirs, "Their scope: the deployment finished", vector);
        await InsertDocumentWithVector(theirs, "Their scope: the deployment is pending", vector);

        var scoped = await _repository.SearchBySimilarityAsync(vector, maxResults: 5, minSimilarity: 0.0, scopeId: mine);

        Assert.Single(scoped);
        Assert.Equal(mineDoc.Id, scoped[0].Document.Id);
        Assert.All(scoped, r => Assert.Equal(mine, r.Document.ScopeId));
    }

    [Fact]
    public async Task SearchBySimilarityAsync_WithoutAScope_StillSearchesEveryScope()
    {
        // Documents the unscoped behaviour rather than endorsing it — the parameter is optional, so
        // a caller that forgets it gets a cross-scope search with no error.
        var mine = CreateTrackedScopeId();
        var theirs = CreateTrackedScopeId();
        var vector = CreateUniformVector(0.25f);

        await InsertDocumentWithVector(mine, "Mine", vector);
        await InsertDocumentWithVector(theirs, "Theirs", vector);

        var unscoped = await _repository.SearchBySimilarityAsync(vector, maxResults: 5, minSimilarity: 0.0);

        Assert.Contains(unscoped, r => r.Document.ScopeId == mine);
        Assert.Contains(unscoped, r => r.Document.ScopeId == theirs);
    }

    [Fact]
    public async Task SearchBySimilarityAsync_ScopedSearch_ReturnsEveryQualifyingRowUpToMaxResults()
    {
        // Detector for the filtered-ANN cliff documented in EfCoreKnowledgeDocumentRepository: the
        // HNSW index covers `vector` only, so once the corpus is large enough for the planner to
        // choose it, a scope predicate most rows fail can silently return FEWER than maxResults even
        // though more of that scope's rows qualify. This test is what fails when that stops being
        // true; the fix at that point is the MATERIALIZED CTE this repository already applies for the
        // scoped path.
        var mine = CreateTrackedScopeId();
        var noise = CreateTrackedScopeId();
        var vector = CreateUniformVector(0.75f);

        const int mineCount = 4;
        for (int i = 0; i < mineCount; i++)
            await InsertDocumentWithVector(mine, $"Mine {i}", vector);

        // Bulk of the corpus belongs to someone else, which is what pushes an ANN scan to
        // exhaust its candidate window on non-matching rows.
        for (int i = 0; i < 30; i++)
            await InsertDocumentWithVector(noise, $"Noise {i}", vector);

        var scoped = await _repository.SearchBySimilarityAsync(vector, maxResults: 5, minSimilarity: 0.0, scopeId: mine);

        Assert.Equal(mineCount, scoped.Count);
        Assert.All(scoped, r => Assert.Equal(mine, r.Document.ScopeId));
    }

    [Fact]
    public async Task SearchBySimilarityAsync_NoDocumentsWithEmbedding_ReturnsEmpty()
    {
        // Arrange — insert a document WITHOUT embedding
        var scopeId = CreateTrackedScopeId();
        var doc = KnowledgeDocument.Create(scopeId, DocumentContent.Create("No embedding doc", "Test"));
        await _repository.AddAsync(doc);

        // Act — search (WHERE vector IS NOT NULL filters this out), scoped to this test's scope.
        // The claim under test is "a document with no embedding is never returned". Only a scoped
        // search can assert that as emptiness: unscoped, any other scope's embedded document is a
        // legitimate hit and Assert.Empty would fail for a reason unrelated to this test.
        var queryVector = CreateUniformVector(1.0f);
        var results = await _repository.SearchBySimilarityAsync(
            queryVector, maxResults: 5, minSimilarity: 0.0, scopeId: scopeId);

        // Assert — no results should be returned because no documents have embeddings
        Assert.Empty(results);
    }
}
