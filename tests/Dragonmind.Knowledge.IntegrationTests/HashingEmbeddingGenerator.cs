using System.Text;

using Microsoft.Extensions.AI;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Deterministic stand-in for a real embedding provider, used only so
/// <see cref="Dragonmind.Core.AI.ExtensionsAIEmbeddingGenerator"/> — the shipped
/// Microsoft.Extensions.AI adapter — has a real (if synthetic)
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> to wrap in the end-to-end tests, rather than
/// a mock standing in for the whole embedding pipeline.
/// <para>
/// Lowercases the input, splits it into alphanumeric tokens, and hashes each token with FNV-1a
/// (never <see cref="string.GetHashCode()"/>, whose result is randomized per process and would make
/// this generator return a different vector for identical text across two test runs) into one of
/// <see cref="Dimensions"/> buckets, then L2-normalizes the result. Two texts sharing vocabulary
/// produce vectors with positive cosine similarity; texts with disjoint vocabularies produce vectors
/// with little to no overlap — good enough to exercise ranking and scope-filtering behavior without
/// calling out to a real model.
/// </para>
/// </summary>
internal sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 1536;

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var embeddings = values.Select(value => new Embedding<float>(HashToVector(value)));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose: this generator holds no unmanaged or external resources.
    }

    private static float[] HashToVector(string text)
    {
        var vector = new float[Dimensions];

        foreach (var token in Tokenize(text))
        {
            var bucket = (int)(Fnv1a(token) % Dimensions);
            vector[bucket] += 1f;
        }

        Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>
    /// FNV-1a, chosen specifically because it is stable across processes and runs — unlike
    /// <see cref="string.GetHashCode()"/>, which is randomized per process for security reasons and
    /// would break the "same text in, same vector out" property these tests rely on.
    /// </summary>
    private static uint Fnv1a(string token)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    /// <summary>
    /// L2-normalizes in place, guarding against a zero-length vector (the empty/whitespace-token
    /// case) dividing by zero and producing a vector full of NaN.
    /// </summary>
    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        var norm = Math.Sqrt(sumOfSquares);
        if (norm == 0d)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}
