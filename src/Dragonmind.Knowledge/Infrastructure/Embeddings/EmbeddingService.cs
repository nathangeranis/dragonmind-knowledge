using Dragonmind.Knowledge.Domain.DomainServices;

namespace Dragonmind.Knowledge.Infrastructure.Embeddings;

/// <summary>
/// Production embedding service that generates real semantic embeddings
/// using the shared <see cref="IEmbeddingGenerator"/>.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator _embeddingGenerator;

    /// <summary>
    /// Initializes a new instance of <see cref="EmbeddingService"/> using the
    /// shared <see cref="IEmbeddingGenerator"/>.
    /// </summary>
    public EmbeddingService(IEmbeddingGenerator embeddingGenerator)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
    }

    public async Task<Embedding> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, nameof(text));

        var vector = await _embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken);

        if (vector == null || vector.Length == 0)
        {
            throw new InvalidOperationException("Embedding service returned null or empty vector");
        }

        return Embedding.Create(vector);
    }
}
