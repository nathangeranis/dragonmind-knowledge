using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Tests that the Apache AGE repository correctly rejects Cypher injection attempts.
/// These tests validate both the predicate allowlist and the entity-name character allowlist.
/// </summary>
public class CypherInjectionTests
{
    /// <summary>
    /// Thrown when a call reaches the database boundary. Rejected input must never get this far
    /// (it throws ArgumentException first); for accepted input, seeing this is the proof that
    /// validation ran and passed. No live database is touched either way.
    /// </summary>
    private const string ReachedDatabaseBoundary = "Reached the database boundary (validation passed).";

    private static ApacheAgeKnowledgeGraphRepository CreateRepository()
    {
        // We only need the repository to fail fast on validation before reaching the DB.
        var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(ReachedDatabaseBoundary));
        var logger = NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance;
        return new ApacheAgeKnowledgeGraphRepository(mockFactory.Object, logger);
    }

    // ── Predicate allowlist tests ────────────────────────────────────────────────

    [Theory]
    [InlineData("UNKNOWN_RELATIONSHIP")]
    [InlineData("MATCH")]
    [InlineData("DELETE FROM")]
    [InlineData("DETACH DELETE")]
    [InlineData("DROP")]
    [InlineData("'; DETACH DELETE n RETURN '")]
    [InlineData("INVALID_PRED")]
    public async Task AddAsync_UnknownPredicate_ThrowsArgumentException(string predicate)
    {
        // Arrange
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Checkout Service", "Service");
        var obj = GraphEntity.Create("Payments DB", "Service");
        var fact = KnowledgeFact.Create(scopeId, subject, predicate, obj);

        // Act & Assert
        // The exception must be thrown before any DB access (no DB calls expected).
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.AddAsync(fact));

        Assert.Contains("Unknown relationship type", ex.Message);
    }

    [Theory]
    [InlineData("DEPENDS_ON")]
    [InlineData("LOCATED_IN")]
    [InlineData("PART_OF")]
    [InlineData("OWNS")]
    [InlineData("CREATED_BY")]
    [InlineData("IS_A")]
    [InlineData("MEMBER_OF")]
    [InlineData("RELATED_TO")]
    [InlineData("CONTAINS")]
    [InlineData("depends_on")]     // case-insensitive
    [InlineData("located in")]     // space-separated form normalised to LOCATED_IN
    public async Task AddAsync_KnownPredicate_DoesNotThrowArgumentException(string predicate)
    {
        // Arrange
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Checkout Service", "Service");
        var obj = GraphEntity.Create("Payments DB", "Service");
        var fact = KnowledgeFact.Create(scopeId, subject, predicate, obj);

        // Act
        var ex = await Record.ExceptionAsync(() => repo.AddAsync(fact));

        // Assert the predicate reached the database boundary, rather than merely that it did
        // not throw ArgumentException: the weaker form also passes when validation faults
        // unexpectedly, or when AddAsync returns without doing anything at all.
        Assert.True(
            ex is InvalidOperationException { Message: ReachedDatabaseBoundary },
            $"Predicate '{predicate}' should pass validation and reach the database boundary, but got: "
                + (ex?.ToString() ?? "no exception"));
    }

    // ── LOCATED_IN regression: \b word-boundary false-positive fix ───────────────

    [Fact]
    public async Task AddAsync_LocatedInPredicate_NotFalselyRejectedByKeywordCheck()
    {
        // LOCATED_IN contains no banned keywords when split on '_':
        // tokens = ["LOCATED", "IN"] — neither is in the suspicious-keyword list.
        // This test guards against regressions where LOCATED_IN was wrongly blocked.
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var fact = KnowledgeFact.Create(
            scopeId,
            GraphEntity.Create("Checkout Service", "Service"),
            "LOCATED_IN",
            GraphEntity.Create("us-east-1", "Region"));

        var ex = await Record.ExceptionAsync(() => repo.AddAsync(fact));

        Assert.True(
            ex is InvalidOperationException { Message: ReachedDatabaseBoundary },
            $"LOCATED_IN should pass validation and reach the database boundary, but got: "
                + (ex?.ToString() ?? "no exception"));
    }

    // ── Entity-name injection: character allowlist tests ────────────────────────

    [Theory]
    [InlineData("Checkout Service'; DETACH DELETE n RETURN '")]
    [InlineData("Payments DB]; DROP TABLE users; --")]
    [InlineData("Entity\nWith\nNewlines")]
    [InlineData("Entity\twith\ttabs")]
    [InlineData("Entity{injection}")]
    [InlineData("Entity$injection")]
    [InlineData("Entity@injection")]
    [InlineData("Entity\\injection")]   // backslash — removed escape branch guards this
    [InlineData("Entity\"injection")]   // double-quote — removed escape branch guards this
    public async Task AddAsync_EntityNameWithDisallowedCharacters_ThrowsArgumentException(string maliciousName)
    {
        // Arrange
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        // GraphEntity.Create only trims, it doesn't validate for Cypher safety.
        var subject = GraphEntity.Create(maliciousName, "Service");
        var obj = GraphEntity.Create("Payments DB", "Service");
        // Use a known-good predicate so the predicate check does not fire first.
        var fact = KnowledgeFact.Create(scopeId, subject, "DEPENDS_ON", obj);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.AddAsync(fact));

        // The exception can come from the character allowlist or keyword check.
        Assert.True(
            ex.Message.Contains("not allowed") || ex.Message.Contains("reserved keyword"),
            $"Unexpected message: {ex.Message}");
    }

    [Theory]
    [InlineData("Service MATCH")]        // token MATCH is a keyword
    [InlineData("Database DELETE me")]   // token DELETE is a keyword
    [InlineData("Team-OF-CREATE")]       // token CREATE is a keyword
    public async Task AddAsync_EntityNameContainingKeywordToken_ThrowsArgumentException(string maliciousName)
    {
        // Arrange — names that pass the character allowlist but contain keyword tokens
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create(maliciousName, "Service");
        var obj = GraphEntity.Create("Payments DB", "Service");
        var fact = KnowledgeFact.Create(scopeId, subject, "KNOWS", obj);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.AddAsync(fact));

        Assert.Contains("reserved keyword", ex.Message);
    }

    // ── Single-quote entity names: apostrophes are allowed ──────────────────────

    [Theory]
    [InlineData("O'Brien Analytics")]
    [InlineData("d'Artagnan Logistics")]
    [InlineData("O'Hare Analytics")]
    public async Task AddAsync_EntityNameWithSingleQuote_PassesValidation(string entityName)
    {
        // Arrange — apostrophes are in the character allowlist (^[a-zA-Z0-9 '_\-.]+$)
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create(entityName, "Service");
        var obj = GraphEntity.Create("Payments DB", "Service");
        var fact = KnowledgeFact.Create(scopeId, subject, "OWNS", obj);

        // Act — validation should pass; the call fails later on mock DB (not ArgumentException)
        var ex = await Record.ExceptionAsync(() => repo.AddAsync(fact));

        Assert.True(
            ex is null || ex is not ArgumentException,
            $"Expected no ArgumentException for entity '{entityName}', but got: {ex}");
    }

    [Theory]
    [InlineData("O'Brien Analytics")]
    [InlineData("d'Artagnan Logistics")]
    public async Task AddAsync_ObjectNameWithSingleQuote_PassesValidation(string objectName)
    {
        // Arrange — apostrophes allowed in the @object position too
        var repo = CreateRepository();
        var scopeId = ScopeId.New();
        var subject = GraphEntity.Create("Checkout Service", "Service");
        var obj = GraphEntity.Create(objectName, "Service");
        var fact = KnowledgeFact.Create(scopeId, subject, "KNOWS", obj);

        // Act
        var ex = await Record.ExceptionAsync(() => repo.AddAsync(fact));

        Assert.True(
            ex is null || ex is not ArgumentException,
            $"Expected no ArgumentException for object '{objectName}', but got: {ex}");
    }
}
