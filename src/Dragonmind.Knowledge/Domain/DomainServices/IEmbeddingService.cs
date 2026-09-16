namespace Dragonmind.Knowledge.Domain.DomainServices;

/// <summary>
/// Service for generating embeddings for text.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for the given text.
    /// </summary>
    Task<Embedding> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}
