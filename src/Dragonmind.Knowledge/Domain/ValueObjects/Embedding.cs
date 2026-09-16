using Dragonmind.Core.Domain;

namespace Dragonmind.Knowledge.Domain.ValueObjects;

/// <summary>
/// Represents a vector embedding for semantic search.
/// Wraps a float array representing the vector in high-dimensional space.
/// </summary>
public sealed class Embedding : ValueObject
{
    public const int MinDimensions = 1;
    public const int MaxDimensions = 4096; // Typical max for embedding models

    private Embedding(float[] vector)
    {
        Vector = vector;
    }

    /// <summary>
    /// The vector representation as a float array.
    /// </summary>
    public float[] Vector { get; private set; }

    // Parameterless constructor for EF Core
    private Embedding()
    {
        Vector = Array.Empty<float>();
    }

    /// <summary>
    /// The dimensionality of the embedding.
    /// </summary>
    public int Dimensions => Vector.Length;

    /// <summary>
    /// Creates an embedding from a float array.
    /// </summary>
    public static Embedding Create(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector, nameof(vector));

        if (vector.Length < MinDimensions)
        {
            throw new ArgumentException($"Embedding must have at least {MinDimensions} dimension(s)", nameof(vector));
        }

        if (vector.Length > MaxDimensions)
        {
            throw new ArgumentException($"Embedding cannot exceed {MaxDimensions} dimensions", nameof(vector));
        }

        // Validate all values are finite (not NaN or Infinity)
        if (vector.Any(v => !float.IsFinite(v)))
        {
            throw new ArgumentException("Embedding vector contains invalid values (NaN or Infinity)", nameof(vector));
        }

        return new Embedding((float[])vector.Clone());
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        // Use sequence equality for the vector
        foreach (var value in Vector)
        {
            yield return value;
        }
    }

    public override string ToString() => $"Embedding({Dimensions}D)";
}
