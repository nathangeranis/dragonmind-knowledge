using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Tests that read methods on <see cref="ApacheAgeKnowledgeGraphRepository"/> reject Cypher
/// injection attempts via SanitizeCypher. All read methods validate input BEFORE opening any DB
/// connection, so a mocked <see cref="IDbContextFactory{KnowledgeDbContext}"/> is sufficient — the
/// factory is never called because SanitizeCypher throws first.
///
/// No external PostgreSQL instance or NpgsqlDataSource is required.
/// </summary>
public class CypherInjectionReadMethodTests
{
    private static readonly ScopeId ValidScopeId = ScopeId.New();

    private static ApacheAgeKnowledgeGraphRepository CreateRepository()
    {
        // SanitizeCypher throws before CreateDbContextAsync is reached, so the mock
        // factory is never invoked. Verifiable() is intentionally omitted — calling
        // the factory would indicate the sanitization gate was bypassed.
        var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>(MockBehavior.Strict);
        var logger = NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance;
        return new ApacheAgeKnowledgeGraphRepository(mockFactory.Object, logger);
    }

    // ── GetFactsBySubjectAsync ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Checkout Service'; DETACH DELETE n RETURN '")]
    [InlineData("Entity{injection}")]
    [InlineData("Service MATCH")]
    public async Task GetFactsBySubjectAsync_InjectionAttempt_ThrowsArgumentException(string maliciousInput)
    {
        var repo = CreateRepository();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.GetFactsBySubjectAsync(maliciousInput, ValidScopeId));

        Assert.True(
            ex.Message.Contains("not allowed") || ex.Message.Contains("reserved keyword"),
            $"Unexpected message: {ex.Message}");
    }

    // ── GetFactsByObjectAsync ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Checkout Service'; DETACH DELETE n RETURN '")]
    [InlineData("Entity{injection}")]
    [InlineData("Database DELETE me")]
    public async Task GetFactsByObjectAsync_InjectionAttempt_ThrowsArgumentException(string maliciousInput)
    {
        var repo = CreateRepository();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.GetFactsByObjectAsync(maliciousInput, ValidScopeId));

        Assert.True(
            ex.Message.Contains("not allowed") || ex.Message.Contains("reserved keyword"),
            $"Unexpected message: {ex.Message}");
    }

    // ── GetFactsByEntityAsync ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Checkout Service'; DETACH DELETE n RETURN '")]
    [InlineData("Entity$injection")]
    [InlineData("Team-OF-CREATE")]
    public async Task GetFactsByEntityAsync_InjectionAttempt_ThrowsArgumentException(string maliciousInput)
    {
        var repo = CreateRepository();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.GetFactsByEntityAsync(maliciousInput, ValidScopeId));

        Assert.True(
            ex.Message.Contains("not allowed") || ex.Message.Contains("reserved keyword"),
            $"Unexpected message: {ex.Message}");
    }

    // ── GetFactsWithinDistanceAsync ─────────────────────────────────────────────

    [Theory]
    [InlineData("Checkout Service'; DETACH DELETE n RETURN '")]
    [InlineData("Entity{injection}")]
    [InlineData("Team-OF-CREATE")]
    public async Task GetFactsWithinDistanceAsync_InjectionAttempt_ThrowsArgumentException(string maliciousInput)
    {
        var repo = CreateRepository();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.GetFactsWithinDistanceAsync(maliciousInput, ValidScopeId));

        Assert.True(
            ex.Message.Contains("not allowed") || ex.Message.Contains("reserved keyword"),
            $"Unexpected message: {ex.Message}");
    }
}
