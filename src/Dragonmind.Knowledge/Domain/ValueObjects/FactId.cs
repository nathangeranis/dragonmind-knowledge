using Dragonmind.Core.Domain;

namespace Dragonmind.Knowledge.Domain.ValueObjects;

/// <summary>
/// Strongly-typed identifier for a knowledge fact in the graph.
/// </summary>
public sealed class FactId : ValueObject
{
    private FactId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static FactId New() => new(Guid.NewGuid());

    public static FactId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Fact ID cannot be empty", nameof(value));
        }

        return new FactId(value);
    }

    public static bool TryParse(string? value, out FactId? factId)
    {
        factId = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        factId = new FactId(guid);
        return true;
    }

    public static implicit operator Guid(FactId factId) => factId.Value;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}
