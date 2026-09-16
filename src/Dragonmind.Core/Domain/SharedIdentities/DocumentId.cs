namespace Dragonmind.Core.Domain.SharedIdentities;

/// <summary>
/// Strongly-typed identifier for a knowledge document.
/// </summary>
public sealed class DocumentId : StronglyTypedId<DocumentId>
{
    private DocumentId(Guid value) : base(value)
    {
    }

    /// <summary>
    /// Creates a DocumentId from an existing Guid value.
    /// </summary>
    public static DocumentId From(Guid value) => CreateFrom(value);

    /// <summary>
    /// Creates a new DocumentId with a new Guid value.
    /// </summary>
    public static DocumentId New() => NewId();
}
