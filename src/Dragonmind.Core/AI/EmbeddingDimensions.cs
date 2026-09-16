namespace Dragonmind.Core.AI;

/// <summary>
/// The embedding vector width every provider registered behind <see cref="IEmbeddingGenerator"/>
/// must produce. This must match the width of the <c>vector(n)</c> pgvector column embeddings are
/// persisted into — a generator that returns a different width is a configuration bug, not a
/// runtime edge case, and <see cref="ExtensionsAIEmbeddingGenerator"/> throws rather than let a
/// mismatched vector reach storage.
/// </summary>
public static class EmbeddingDimensions
{
    /// <summary>
    /// The default embedding width (1536), matching the <c>vector(1536)</c> column used for
    /// semantic search over knowledge documents.
    /// </summary>
    public const int Default = 1536;
}
