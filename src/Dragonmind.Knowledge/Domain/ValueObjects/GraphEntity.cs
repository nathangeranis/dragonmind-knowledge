using Dragonmind.Core.Domain;

namespace Dragonmind.Knowledge.Domain.ValueObjects;

/// <summary>
/// Represents an entity in the knowledge graph (e.g., Service, Team, Component).
/// </summary>
public sealed class GraphEntity : ValueObject
{
    private GraphEntity(string name, string entityType)
    {
        Name = name;
        EntityType = entityType;
    }

    /// <summary>
    /// The name of the entity (e.g., "Checkout Service", "Payments DB").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The type of entity (e.g., "Service", "Team", "Component").
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// Creates a graph entity with validation.
    /// </summary>
    public static GraphEntity Create(string name, string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType, nameof(entityType));

        return new GraphEntity(name.Trim(), entityType.Trim());
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Name;
        yield return EntityType;
    }

    public override string ToString() => $"{Name} ({EntityType})";
}
