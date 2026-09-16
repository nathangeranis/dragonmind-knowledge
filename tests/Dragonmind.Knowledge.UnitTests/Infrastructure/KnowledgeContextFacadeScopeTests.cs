using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Application.DTOs;
using Dragonmind.Knowledge.Application.Queries.SearchKnowledge;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;

using MediatR;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Guards the scope on knowledge retrieval.
/// <para>
/// The similarity search ranks rows purely by cosine distance. Without a <c>scope_id</c>
/// predicate in the SQL, every scope in the database would compete for the same result slots and
/// results would come back however weakly they matched — one busy scope's documents could evict
/// another scope's documents from its own caller's context.
/// </para>
/// <para>
/// The facade REQUIRES the parameter, so omitting it is a compile error rather than a silent
/// cross-scope search; passing <c>null</c> is a deliberate opt-in to searching every scope. What
/// the compiler cannot catch is the id being dropped or mistranslated while it is threaded on to
/// <see cref="SearchKnowledgeQuery"/> — that still fails silently and globally, and is what these
/// tests pin.
/// </para>
/// </summary>
public class KnowledgeContextFacadeScopeTests
{
    private static readonly Guid ScopeGuid = Guid.Parse("bf6acaab-8799-e122-ee9d-86f62b4d71e8");

    private static (KnowledgeContextFacade Facade, List<SearchKnowledgeQuery> Dispatched) BuildFacade()
    {
        var dispatched = new List<SearchKnowledgeQuery>();
        var mediator = new Mock<IMediator>();

        // MediatR declares Send<TResponse>(IRequest<TResponse>, CancellationToken), so the callback
        // has to be typed to the DECLARED parameter — a Callback<SearchKnowledgeQuery, ...> is
        // rejected at setup time with "Invalid callback", not at assert time.
        mediator
            .Setup(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<IReadOnlyList<VectorSearchResultDto>>, CancellationToken>(
                (q, _) => dispatched.Add((SearchKnowledgeQuery)q))
            .ReturnsAsync((IReadOnlyList<VectorSearchResultDto>)new List<VectorSearchResultDto>());

        return (new KnowledgeContextFacade(mediator.Object), dispatched);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_PassesTheScopeIdIntoTheQuery()
    {
        var (facade, dispatched) = BuildFacade();

        await facade.SearchKnowledgeAsync("checkout failures", ScopeId.From(ScopeGuid), maxResults: 5);

        var query = Assert.Single(dispatched);
        Assert.Equal(ScopeGuid, query.ScopeId);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_WithoutAScopeSearchesEveryScope()
    {
        // Documents the unscoped behaviour rather than endorsing it: a null id means no scope
        // predicate reaches the SQL. The parameter is REQUIRED, so this is a deliberate choice at
        // the call site rather than something a caller can fall into by omission.
        var (facade, dispatched) = BuildFacade();

        await facade.SearchKnowledgeAsync("checkout failures", scopeId: null, maxResults: 5);

        var query = Assert.Single(dispatched);
        Assert.Null(query.ScopeId);
    }

    [Fact]
    public void SearchKnowledgeQuery_CarriesTheScopeIdItWasConstructedWith()
    {
        var query = new SearchKnowledgeQuery("checkout failures", maxResults: 5, minSimilarity: 0.0, scopeId: ScopeGuid);

        Assert.Equal(ScopeGuid, query.ScopeId);
    }
}
