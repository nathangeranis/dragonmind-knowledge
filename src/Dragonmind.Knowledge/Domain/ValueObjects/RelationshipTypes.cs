using System.Collections.Frozen;

namespace Dragonmind.Knowledge.Domain.ValueObjects;

/// <summary>
/// The knowledge graph's relationship vocabulary: every predicate the graph will store.
///
/// This doubles as the injection boundary: Apache AGE cannot parameterize a relationship
/// type, so labels are interpolated into Cypher unquoted and only membership of this set
/// makes that safe. Entries must always be bare uppercase identifiers.
/// </summary>
public static class RelationshipTypes
{
    /// <summary>
    /// Every relationship type the graph will store.
    ///
    /// Frozen rather than a plain HashSet: a security boundary exposed as IReadOnlySet can
    /// otherwise be cast back to HashSet and mutated at runtime. FrozenSet refuses.
    /// </summary>
    public static IReadOnlySet<string> Allowed { get; } = new[]
    {
        "IS_A", "PART_OF", "LOCATED_IN", "DEPENDS_ON", "OWNS", "CREATED_BY",
        "MEMBER_OF", "RELATED_TO", "REFERENCES", "CONTAINS", "REPLACES", "KNOWS"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a predicate to its canonical graph form, so that "located in" and "Located_In"
    /// resolve to the same edge label.
    /// </summary>
    public static string Normalize(string predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate, nameof(predicate));
        return predicate.Trim().Replace(" ", "_").ToUpperInvariant();
    }

    /// <summary>
    /// Whether the graph will accept this predicate, in any casing or spaced form.
    /// </summary>
    public static bool IsAllowed(string? predicate)
        => !string.IsNullOrWhiteSpace(predicate) && Allowed.Contains(Normalize(predicate));
}
