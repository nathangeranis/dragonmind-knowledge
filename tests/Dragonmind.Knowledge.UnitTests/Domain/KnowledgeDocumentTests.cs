using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.UnitTests.Domain;

/// <summary>
/// Unit tests for the <see cref="KnowledgeDocument"/> aggregate.
/// Tests creation, embedding management, and reconstitution from a repository.
/// </summary>
public class KnowledgeDocumentTests
{
    [Fact]
    public void Create_ValidData_CreatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("The Checkout Service retries a failed payment up to three times.");

        // Act
        var document = KnowledgeDocument.Create(scopeId, content);

        // Assert
        Assert.NotNull(document);
        Assert.NotEqual(Guid.Empty, document.Id.Value);
        Assert.Equal(scopeId, document.ScopeId);
        Assert.Equal(content, document.Content);
        Assert.Null(document.Embedding);
        Assert.True(document.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public void CreateWithEmbedding_ValidData_CreatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("The Inventory Service reserves stock for ten minutes.");
        var embedding = Embedding.Create(new float[] { 0.1f, 0.2f, 0.3f });

        // Act
        var document = KnowledgeDocument.CreateWithEmbedding(scopeId, content, embedding);

        // Assert
        Assert.NotNull(document);
        Assert.Equal(content, document.Content);
        Assert.NotNull(document.Embedding);
        Assert.Equal(embedding, document.Embedding);
    }

    [Fact]
    public void SetEmbedding_ValidVector_StoresEmbedding()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("The Payments DB replicates to a standby every five seconds.");
        var document = KnowledgeDocument.Create(scopeId, content);
        var embedding = Embedding.Create(new float[] { 0.1f, 0.2f, 0.3f });

        // Act
        document.SetEmbedding(embedding);

        // Assert
        Assert.NotNull(document.Embedding);
        Assert.Equal(embedding, document.Embedding);
    }

    [Fact]
    public void UpdateContent_ValidContent_UpdatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var originalContent = DocumentContent.Create("Original content");
        var document = KnowledgeDocument.Create(scopeId, originalContent);
        var newContent = DocumentContent.Create("Updated content describing the Checkout Service runbook.");

        // Act
        document.UpdateContent(newContent);

        // Assert
        Assert.Equal(newContent, document.Content);
    }

    [Fact]
    public void Reconstitute_ValidData_CreatesDocument()
    {
        // Arrange
        var id = DocumentId.New();
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("The architecture-notes describe the retry policy in detail.");
        var embedding = Embedding.Create(new float[] { 0.1f, 0.2f, 0.3f });
        var timestamp = DateTime.UtcNow.AddHours(-1);

        // Act
        var document = KnowledgeDocument.Reconstitute(id, scopeId, content, embedding, timestamp);

        // Assert
        Assert.NotNull(document);
        Assert.Equal(id, document.Id);
        Assert.Equal(scopeId, document.ScopeId);
        Assert.Equal(content, document.Content);
        Assert.Equal(embedding, document.Embedding);
        Assert.Equal(timestamp, document.Timestamp);
    }

    [Fact]
    public void Reconstitute_NullEmbedding_CreatesDocument()
    {
        // Arrange
        var id = DocumentId.New();
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("A follow-up runbook entry with no embedding yet.");
        var timestamp = DateTime.UtcNow.AddHours(-1);

        // Act
        var document = KnowledgeDocument.Reconstitute(id, scopeId, content, null, timestamp);

        // Assert
        Assert.NotNull(document);
        Assert.Null(document.Embedding);
    }

    [Fact]
    public void Create_NullScopeId_ThrowsArgumentNullException()
    {
        // Arrange
        var content = DocumentContent.Create("Test content");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => KnowledgeDocument.Create(null!, content));
    }

    [Fact]
    public void Create_NullContent_ThrowsArgumentNullException()
    {
        // Arrange
        var scopeId = ScopeId.New();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => KnowledgeDocument.Create(scopeId, null!));
    }

    [Fact]
    public void SetEmbedding_NullEmbedding_ThrowsArgumentNullException()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("Test content");
        var document = KnowledgeDocument.Create(scopeId, content);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => document.SetEmbedding(null!));
    }

    [Fact]
    public void UpdateContent_NullContent_ThrowsArgumentNullException()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var content = DocumentContent.Create("Test content");
        var document = KnowledgeDocument.Create(scopeId, content);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => document.UpdateContent(null!));
    }
}
