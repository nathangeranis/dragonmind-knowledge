using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="EfCoreKnowledgeDocumentRepository"/> against a real
/// PostgreSQL + pgvector instance. Tests CRUD operations with actual database persistence.
/// </summary>
[Collection("Postgres")]
public sealed class KnowledgeDocumentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<ScopeId> _testScopeIds = new();
    private EfCoreKnowledgeDocumentRepository _repository = null!;
    private IDbContextFactory<KnowledgeDbContext> _contextFactory = null!;

    public KnowledgeDocumentRepositoryIntegrationTests(PostgresFixture fixture)
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

    [Fact]
    public async Task AddAsync_ValidDocument_PersistsToDatabase()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var content = DocumentContent.Create("The Checkout Service depends on the Payments DB.", "Test", "architecture-notes");
        var document = KnowledgeDocument.Create(scopeId, content);

        // Act
        await _repository.AddAsync(document);
        var retrieved = await _repository.GetByIdAsync(document.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(document.Id, retrieved.Id);
        Assert.Equal("The Checkout Service depends on the Payments DB.", retrieved.Content.Value);
        Assert.Equal("Test", retrieved.Content.Source);
    }

    [Fact]
    public async Task AddAsync_DocumentWithEmbedding_PersistsVectorColumn()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var content = DocumentContent.Create("A new runbook is published.", "Test");
        var vector = new float[1536];
        for (int i = 0; i < 1536; i++) vector[i] = 0.01f * i;
        var embedding = Embedding.Create(vector);
        var document = KnowledgeDocument.CreateWithEmbedding(scopeId, content, embedding);

        // Act
        await _repository.AddAsync(document);
        var retrieved = await _repository.GetByIdAsync(document.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.Embedding);
        Assert.Equal(1536, retrieved.Embedding.Vector.Length);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync(DocumentId.New());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByScopeIdAsync_MultipleDocuments_ReturnsAll()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        for (int i = 0; i < 3; i++)
        {
            var doc = KnowledgeDocument.Create(scopeId, DocumentContent.Create($"Document {i}", "Test"));
            await _repository.AddAsync(doc);
        }

        // Act
        var results = await _repository.GetByScopeIdAsync(scopeId);

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetByScopeIdAsync_DifferentScopes_ReturnsOnlyMatching()
    {
        // Arrange
        var scopeA = CreateTrackedScopeId();
        var scopeB = CreateTrackedScopeId();
        await _repository.AddAsync(KnowledgeDocument.Create(scopeA, DocumentContent.Create("Scope A doc", "Test")));
        await _repository.AddAsync(KnowledgeDocument.Create(scopeA, DocumentContent.Create("Scope A doc 2", "Test")));
        await _repository.AddAsync(KnowledgeDocument.Create(scopeB, DocumentContent.Create("Scope B doc", "Test")));

        // Act
        var resultsA = await _repository.GetByScopeIdAsync(scopeA);

        // Assert
        Assert.Equal(2, resultsA.Count);
        Assert.All(resultsA, doc => Assert.Equal(scopeA, doc.ScopeId));
    }

    [Fact]
    public async Task UpdateAsync_ExistingDocument_PersistsChanges()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var document = KnowledgeDocument.Create(scopeId, DocumentContent.Create("Original content", "Test"));
        await _repository.AddAsync(document);

        // Act — update content
        document.UpdateContent(DocumentContent.Create("Updated content", "Test"));
        await _repository.UpdateAsync(document);
        var retrieved = await _repository.GetByIdAsync(document.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Updated content", retrieved.Content.Value);
    }

    [Fact]
    public async Task DeleteAsync_ExistingDocument_RemovesFromDatabase()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var document = KnowledgeDocument.Create(scopeId, DocumentContent.Create("To be deleted", "Test"));
        await _repository.AddAsync(document);

        // Act
        await _repository.DeleteAsync(document.Id);
        var retrieved = await _repository.GetByIdAsync(document.Id);

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        // Act & Assert — should not throw
        await _repository.DeleteAsync(DocumentId.New());
    }
}
