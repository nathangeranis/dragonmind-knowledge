using Dragonmind.Core.Application;
using Dragonmind.Knowledge.Application.DTOs;

namespace Dragonmind.Knowledge.Application.Queries.SearchKnowledge;

/// <summary>
/// Query to search for knowledge documents using semantic similarity.
/// </summary>
public sealed record SearchKnowledgeQuery : IQuery<IReadOnlyList<VectorSearchResultDto>>
{
    /// <summary>
    /// The search query text.
    /// </summary>
    public string QueryText { get; init; }

    /// <summary>
    /// The maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; }

    /// <summary>
    /// The minimum similarity score threshold (0.0 to 1.0).
    /// </summary>
    public double MinSimilarity { get; init; }

    /// <summary>
    /// When supplied, restricts the search to documents belonging to that scope. Null searches
    /// every scope's documents — results are ranked purely by cosine distance, so one scope's
    /// documents then compete for the same <see cref="MaxResults"/> slots as another's.
    /// </summary>
    public Guid? ScopeId { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchKnowledgeQuery"/> record.
    /// </summary>
    /// <param name="queryText">The search query text. Must not be null or whitespace.</param>
    /// <param name="maxResults">The maximum number of results to return. Must be positive.</param>
    /// <param name="minSimilarity">The minimum similarity score threshold. Must be between 0.0 and 1.0.</param>
    /// <param name="scopeId">When supplied, restricts the search to that scope's documents.</param>
    /// <exception cref="ArgumentException">Thrown when queryText is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when maxResults is not positive or minSimilarity is not in valid range.</exception>
    public SearchKnowledgeQuery(string queryText, int maxResults = 5, double minSimilarity = 0.0, Guid? scopeId = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new ArgumentException("QueryText must not be null or whitespace.", nameof(queryText));
        }

        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), maxResults, "MaxResults must be positive.");
        }

        if (minSimilarity < 0.0 || minSimilarity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), minSimilarity, "MinSimilarity must be between 0.0 and 1.0.");
        }

        QueryText = queryText;
        MaxResults = maxResults;
        MinSimilarity = minSimilarity;
        ScopeId = scopeId;
    }
}
