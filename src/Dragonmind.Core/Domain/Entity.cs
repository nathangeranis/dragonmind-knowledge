namespace Dragonmind.Core.Domain;

/// <summary>
/// Base class for all entities in the domain model.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>> where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>
    /// Domain events raised by this entity since it was created or loaded.
    /// </summary>
    /// <remarks>
    /// This library raises domain events but deliberately does not dispatch them, in the same way
    /// it declares an embedding seam without shipping a provider. The only safe dispatch point is
    /// after the unit of work commits, and that is the host's decision: collect these from tracked
    /// entities once the save succeeds, publish them, then call <see cref="ClearDomainEvents"/>.
    /// <c>IDomainEventHandler&lt;T&gt;</c> is the matching seam and ships with no implementations here.
    /// </remarks>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    protected void RemoveDomainEvent(IDomainEvent domainEvent) => _domainEvents.Remove(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();

    public bool Equals(Entity<TId>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;

        // If either entity has the default Id (i.e., is transient), do not treat them as equal
        if (EqualityComparer<TId>.Default.Equals(Id, default!) ||
            EqualityComparer<TId>.Default.Equals(other.Id, default!))
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj) => obj is Entity<TId> entity && Equals(entity);

    public override int GetHashCode()
    {
        // For transient entities (default Id), use an instance-based hash code to avoid collisions
        if (EqualityComparer<TId>.Default.Equals(Id, default!))
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }

        return EqualityComparer<TId>.Default.GetHashCode(Id);
    }
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => left?.Equals(right) ?? right is null;
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}

/// <summary>
/// Base class for all aggregate roots.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
}
