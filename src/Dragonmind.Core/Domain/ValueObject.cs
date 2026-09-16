namespace Dragonmind.Core.Domain;

/// <summary>
/// Base class for all value objects in the domain model.
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => obj is ValueObject valueObject && Equals(valueObject);

    public override int GetHashCode()
    {
        return GetEqualityComponents()
            .Aggregate(1, (current, obj) =>
            {
                unchecked
                {
                    return (current * 23) + (obj?.GetHashCode() ?? 0);
                }
            });
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) => left?.Equals(right) ?? right is null;
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);

    /// <summary>
    /// Creates a shallow copy of this value object instance.
    /// </summary>
    /// <remarks>
    /// This method is intended for use by derived value object types when implementing
    /// cloning or "with"-style methods. It uses <see cref="MemberwiseClone"/> under the hood,
    /// so reference-type fields will not be deep-copied.
    /// </remarks>
    /// <returns>A shallow copy of the current <see cref="ValueObject"/>.</returns>
    protected ValueObject GetCopy() => (ValueObject)MemberwiseClone();
}
