using Dragonmind.Core.Domain;

namespace Dragonmind.Knowledge.Domain.ValueObjects;

/// <summary>
/// Represents the content of a knowledge document.
/// Validates that content is not empty and enforces maximum length.
/// </summary>
public sealed class DocumentContent : ValueObject
{
    public const int MaxLength = 10000;
    public const int MinLength = 1;

    private DocumentContent(string value, string? source, string? category)
    {
        Value = value;
        Source = source;
        Category = category;
    }

    /// <summary>
    /// The actual text content of the document.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// The source of the content (e.g., "architecture-notes", "runbook", "ADR-7").
    /// </summary>
    public string? Source { get; private set; }

    /// <summary>
    /// The category of the content (e.g., "Service", "Team", "Policy").
    /// </summary>
    public string? Category { get; private set; }

    // Parameterless constructor for EF Core
    private DocumentContent()
    {
        Value = string.Empty;
    }

    /// <summary>
    /// Creates document content with validation.
    /// </summary>
    public static DocumentContent Create(string content, string? source = null, string? category = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content, nameof(content));

        if (content.Length < MinLength)
        {
            throw new ArgumentException($"Document content must be at least {MinLength} character(s)", nameof(content));
        }

        if (content.Length > MaxLength)
        {
            throw new ArgumentException($"Document content cannot exceed {MaxLength} characters", nameof(content));
        }

        return new DocumentContent(content, source, category);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
        yield return Source ?? string.Empty;
        yield return Category ?? string.Empty;
    }

    public override string ToString() => Value;
}
