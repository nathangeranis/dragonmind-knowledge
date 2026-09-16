using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using IDragonmindEmbeddingGenerator = Dragonmind.Core.AI.IEmbeddingGenerator;

namespace Dragonmind.Core.AI;

/// <summary>
/// Embedding generator backed by a Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> provider.
/// Enforces a fixed output dimensionality so vectors always match the pgvector
/// column width, regardless of whether the provider honors the requested
/// <see cref="EmbeddingGenerationOptions.Dimensions"/>.
/// </summary>
/// <remarks>
/// Dimensionality is enforced in layers:
/// 1. The requested width is passed to the provider via
///    <see cref="EmbeddingGenerationOptions.Dimensions"/>.
/// 2. If the provider ignores that request and returns a longer vector, it is
///    truncated. This is valid for Matryoshka Representation Learning (MRL) trained
///    models, whose output is designed to stay meaningful when truncated to one of a
///    small set of supported widths.
/// 3. The final vector is L2-renormalized (required after truncation; a no-op for
///    already-normalized vectors; harmless for cosine similarity either way).
/// 4. A shorter-than-expected vector cannot be repaired and throws before it can
///    reach the vector store.
/// If a configured provider's request-time width negotiation ever proves inadequate,
/// the escalation path is a provider-specific client that exposes an explicit
/// output-dimensionality parameter, swapped in behind this same seam.
/// </remarks>
public sealed class ExtensionsAIEmbeddingGenerator : IDragonmindEmbeddingGenerator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly int _dimensions;
    private readonly ILogger<ExtensionsAIEmbeddingGenerator> _logger;
    private int _truncationWarned;

    /// <summary>
    /// Creates the generator.
    /// </summary>
    /// <param name="inner">The Microsoft.Extensions.AI embedding provider.</param>
    /// <param name="dimensions">The exact output dimensionality every vector must have (matches the pgvector column).</param>
    /// <param name="logger">Logger.</param>
    public ExtensionsAIEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        int dimensions,
        ILogger<ExtensionsAIEmbeddingGenerator> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions, nameof(dimensions));
        _dimensions = dimensions;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, nameof(text));

        try
        {
            var options = new EmbeddingGenerationOptions { Dimensions = _dimensions };
            var embeddings = await _inner.GenerateAsync([text], options, ct);

            if (embeddings is null || embeddings.Count == 0)
            {
                _logger.LogWarning("Embedding provider returned null or empty result for input text.");
                throw new InvalidOperationException("Embedding provider returned null or empty result.");
            }

            var vector = embeddings[0].Vector.ToArray();

            if (vector.Length > _dimensions)
            {
                if (Interlocked.Exchange(ref _truncationWarned, 1) == 0)
                {
                    _logger.LogWarning(
                        "Embedding provider returned {ActualDimensions} dimensions instead of the requested {ExpectedDimensions}; " +
                        "truncating (valid for MRL-trained models). This warning is logged once per process.",
                        vector.Length, _dimensions);
                }
                vector = vector[.._dimensions];
            }

            if (vector.Length != _dimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding has {vector.Length} dimensions; expected {_dimensions}. " +
                    "A shorter-than-expected vector cannot be repaired and must not reach the vector store.");
            }

            Renormalize(vector);
            return vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to generate embedding for text of length {Length}.", text.Length);
            throw;
        }
    }

    /// <summary>
    /// L2-normalizes the vector in place. Skips degenerate (zero/NaN norm) vectors
    /// and vectors that are already unit-length.
    /// </summary>
    private static void Renormalize(float[] vector)
    {
        double sumOfSquares = 0d;
        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        var norm = Math.Sqrt(sumOfSquares);
        if (norm == 0d || double.IsNaN(norm) || Math.Abs(norm - 1d) < 1e-6)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}
