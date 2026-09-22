using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Shared teardown for the knowledge graph integration tests.
/// <para>
/// Extracted verbatim (no behavior change) from
/// <see cref="AgeKnowledgeGraphRepositoryIntegrationTests.DisposeAsync"/> so that a second test class
/// which creates facts only through <see cref="Dragonmind.Core.Application.AntiCorruptionLayer.IKnowledgeContextFacade"/>
/// — and therefore never sees a <see cref="FactId"/> come back from a write, only a <c>bool</c> — can
/// still clean up with the same belt-and-suspenders shape <see cref="TrackedDocumentCleanup"/> uses
/// for documents: delete every tracked fact by id, then prove the underlying vertices are actually
/// gone by re-querying the graph directly rather than assuming the deletes landed.
/// </para>
/// </summary>
internal static class TrackedFactCleanup
{
    /// <summary>
    /// Deletes every fact in <paramref name="trackedFactIds"/>, then verifies every vertex named in
    /// <paramref name="trackedEntityNames"/> is gone from the graph.
    /// </summary>
    /// <param name="repository">The knowledge graph repository to delete facts through.</param>
    /// <param name="contextFactory">Factory for the Knowledge DbContext, used only for the raw
    /// verification query below — the deletes themselves go through <paramref name="repository"/>.</param>
    /// <param name="trackedFactIds">
    /// Fact ids belonging to the calling test class — either minted by it directly, or read back from
    /// the scopes it created (a test that writes through the facade never sees the id it was assigned).
    /// </param>
    /// <param name="trackedEntityNames">
    /// Entity names minted by the test class itself. Each carries a fresh GUID suffix, so no
    /// pre-existing vertex can share one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a tracked name is not a bare alphanumeric identifier (which would widen the delete
    /// below beyond this test's own vertices), or when at least one tracked vertex is still present in
    /// the graph after cleanup.
    /// </exception>
    public static async Task DeleteTrackedFactsAsync(
        IKnowledgeGraphRepository repository,
        IDbContextFactory<KnowledgeDbContext> contextFactory,
        IReadOnlyCollection<FactId> trackedFactIds,
        IReadOnlyCollection<string> trackedEntityNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(trackedFactIds);
        ArgumentNullException.ThrowIfNull(trackedEntityNames);

        // The DETACH DELETE below interpolates these names into a Cypher list literal, which AGE only
        // accepts as a literal string body — it cannot be parameterized. Every caller mints names as
        // base + Guid("N"), so anything outside that shape means the tracking list has been corrupted,
        // and deleting on it could match vertices this test never created. Refuse by construction, the
        // same way TrackedDocumentCleanup refuses an empty scope id.
        if (trackedEntityNames.Any(name => string.IsNullOrEmpty(name) || !name.All(char.IsLetterOrDigit)))
        {
            throw new InvalidOperationException(
                "Refusing to run knowledge graph cleanup: the tracked entity name list contains a name " +
                "that is not a bare alphanumeric identifier, which would widen the delete beyond the " +
                "vertices this test created.");
        }

        foreach (var factId in trackedFactIds)
        {
            await repository.DeleteAsync(factId, cancellationToken);
        }

        if (trackedEntityNames.Count == 0)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            var nameList = string.Join(", ", trackedEntityNames.Select(n => $"'{n}'"));

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $@"
                    SELECT * FROM cypher('{KnowledgeGraph.Name}', $$
                        MATCH (n:Entity) WHERE n.name IN [{nameList}]
                        DETACH DELETE n
                    $$) as (result agtype);
                ";
                await delete.ExecuteNonQueryAsync(cancellationToken);
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
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            var remaining = await reader.ReadAsync(cancellationToken);
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
}
