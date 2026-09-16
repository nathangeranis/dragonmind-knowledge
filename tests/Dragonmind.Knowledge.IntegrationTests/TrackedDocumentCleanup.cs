using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Shared teardown for the document integration tests.
/// <para>
/// Cleanup is scoped by construction rather than by convention: this helper resolves the tracked
/// scopes to an explicit list of primary keys <em>first</em>, then deletes only those keys, and
/// fails loudly if the delete affects a different number of rows than it targeted. A future edit
/// that widens the predicate therefore trips an assertion instead of quietly deleting unrelated rows.
/// </para>
/// </summary>
internal static class TrackedDocumentCleanup
{
    /// <summary>
    /// Deletes exactly the knowledge documents belonging to <paramref name="trackedScopeIds"/>.
    /// </summary>
    /// <param name="contextFactory">Factory for the Knowledge DbContext.</param>
    /// <param name="trackedScopeIds">
    /// Scope ids minted by the test class itself. Each is a fresh <see cref="ScopeId.New"/> value,
    /// so no pre-existing row can belong to one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a tracked id is empty (which would widen the predicate), or when the delete
    /// affects a different row count than the primary keys it targeted.
    /// </exception>
    public static async Task DeleteTrackedDocumentsAsync(
        IDbContextFactory<KnowledgeDbContext> contextFactory,
        IReadOnlyCollection<ScopeId> trackedScopeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(trackedScopeIds);

        if (trackedScopeIds.Count == 0)
        {
            return;
        }

        // A default/empty id is never produced by ScopeId.New(). If one reaches here the tracking
        // list has been corrupted, and deleting on it could match unrelated rows.
        if (trackedScopeIds.Any(id => id.Value == Guid.Empty))
        {
            throw new InvalidOperationException(
                "Refusing to run document cleanup: the tracked scope list contains an empty id, " +
                "which would widen the delete beyond rows this test created.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Resolve to concrete primary keys before deleting. The delete below can then only ever
        // affect rows this query already identified as belonging to a tracked scope.
        var documentIds = await context.Documents
            .Where(d => trackedScopeIds.Contains(d.ScopeId))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        if (documentIds.Count == 0)
        {
            return;
        }

        var deleted = await context.Documents
            .Where(d => documentIds.Contains(d.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted != documentIds.Count)
        {
            throw new InvalidOperationException(
                $"Document cleanup targeted {documentIds.Count} document(s) by primary key but " +
                $"deleted {deleted}. The delete predicate no longer matches the rows it selected — " +
                "treat this as a cleanup-scoping defect, not a flaky test.");
        }
    }
}
