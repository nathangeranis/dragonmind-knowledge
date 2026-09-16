namespace Dragonmind.Knowledge.Application.DTOs;

/// <summary>
/// DTO for vector search results with relevance score.
/// </summary>
public class VectorSearchResultDto
{
    public VectorSearchResultDto(
        KnowledgeDocumentDto document,
        double similarityScore)
    {
        ArgumentNullException.ThrowIfNull(document, nameof(document));

        // Similarity derived from L2 distance via 1 / (1 + distance) ranges from 0 to 1
        if (similarityScore < 0.0 || similarityScore > 1.0)
        {
            throw new ArgumentException("Similarity score must be between 0.0 and 1.0", nameof(similarityScore));
        }

        Document = document;
        SimilarityScore = similarityScore;
    }

    public KnowledgeDocumentDto Document { get; }
    public double SimilarityScore { get; }
}
