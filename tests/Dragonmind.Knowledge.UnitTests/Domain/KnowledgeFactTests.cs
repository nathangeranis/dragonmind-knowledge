using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.UnitTests.Domain;

/// <summary>
/// Unit tests for the <see cref="KnowledgeFact"/> aggregate.
/// Tests creation, reconstitution, and validation of knowledge graph facts.
/// </summary>
public class KnowledgeFactTests
{
    [Fact]
    public void Create_ValidData_CreatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Checkout Service", "Service");
        var predicate = "DEPENDS_ON";
        var obj = GraphEntity.Create("Payments DB", "Service");

        // Act
        var fact = KnowledgeFact.Create(scopeId, subject, predicate, obj);

        // Assert
        Assert.NotNull(fact);
        Assert.NotNull(fact.Id);
        Assert.Equal(scopeId, fact.ScopeId);
        Assert.Equal(subject, fact.Subject);
        Assert.Equal(predicate, fact.Predicate);
        Assert.Equal(obj, fact.Object);
        Assert.True(fact.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public void CreateFromStrings_ValidData_CreatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();

        // Act
        var fact = KnowledgeFact.CreateFromStrings(
            scopeId,
            "Inventory Service",
            "Service",
            "LOCATED_IN",
            "us-east-1",
            "Region");

        // Assert
        Assert.NotNull(fact);
        Assert.Equal("Inventory Service", fact.Subject.Name);
        Assert.Equal("Service", fact.Subject.EntityType);
        Assert.Equal("LOCATED_IN", fact.Predicate);
        Assert.Equal("us-east-1", fact.Object.Name);
        Assert.Equal("Region", fact.Object.EntityType);
    }

    [Fact]
    public void Create_OwnershipRelationship_CreatesSuccessfully()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("ADR-7", "Document");
        var predicate = "OWNS";
        var obj = GraphEntity.Create("Checkout Team", "Team");

        // Act
        var fact = KnowledgeFact.Create(scopeId, subject, predicate, obj);

        // Assert
        Assert.NotNull(fact);
        Assert.Equal("ADR-7", fact.Subject.Name);
        Assert.Equal("Document", fact.Subject.EntityType);
        Assert.Equal("OWNS", fact.Predicate);
    }

    [Fact]
    public void Reconstitute_ValidData_CreatesFact()
    {
        // Arrange
        var factId = FactId.New();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Payments DB", "Service");
        var predicate = "CREATED_BY";
        var obj = GraphEntity.Create("Platform Team", "Team");
        var timestamp = DateTime.UtcNow.AddHours(-2);

        // Act
        var fact = KnowledgeFact.Reconstitute(factId, scopeId, subject, predicate, obj, timestamp);

        // Assert
        Assert.NotNull(fact);
        Assert.Equal(factId, fact.Id);
        Assert.Equal(scopeId, fact.ScopeId);
        Assert.Equal(subject, fact.Subject);
        Assert.Equal(predicate, fact.Predicate);
        Assert.Equal(obj, fact.Object);
        Assert.Equal(timestamp, fact.Timestamp);
    }

    [Fact]
    public void Create_NullScopeId_ThrowsArgumentNullException()
    {
        // Arrange
        var subject = GraphEntity.Create("Test", "Type");
        var predicate = "RELATED_TO";
        var obj = GraphEntity.Create("Target", "Type");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => KnowledgeFact.Create(null!, subject, predicate, obj));
    }

    [Fact]
    public void Create_NullSubject_ThrowsArgumentNullException()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var predicate = "RELATED_TO";
        var obj = GraphEntity.Create("Target", "Type");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => KnowledgeFact.Create(scopeId, null!, predicate, obj));
    }

    [Fact]
    public void Create_NullOrBlankPredicate_ThrowsArgumentException()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Source", "Type");
        var obj = GraphEntity.Create("Target", "Type");

        // Act & Assert
        // Null predicate throws ArgumentNullException (not ArgumentException)
        Assert.Throws<ArgumentNullException>(() => KnowledgeFact.Create(scopeId, subject, null!, obj));
        // Empty/whitespace predicates throw ArgumentException
        Assert.Throws<ArgumentException>(() => KnowledgeFact.Create(scopeId, subject, "", obj));
        Assert.Throws<ArgumentException>(() => KnowledgeFact.Create(scopeId, subject, "   ", obj));
    }

    [Fact]
    public void Create_NullObject_ThrowsArgumentNullException()
    {
        // Arrange
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Source", "Type");
        var predicate = "RELATED_TO";

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => KnowledgeFact.Create(scopeId, subject, predicate, null!));
    }
}
