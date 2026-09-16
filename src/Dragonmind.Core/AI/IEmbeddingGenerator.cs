namespace Dragonmind.Core.AI;

/// <summary>
/// Context-agnostic embedding generation service.
/// Any bounded context can depend on this for generating text embeddings
/// without coupling to a specific AI provider.
/// </summary>
public interface IEmbeddingGenerator
{
    /// <summary>
    /// Generates an embedding vector for the given text input.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A float array representing the embedding vector.</returns>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
