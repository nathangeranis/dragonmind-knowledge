using System.Globalization;

using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

/// <summary>
/// One row of the knowledge graph fact projection, already materialised as text.
/// </summary>
/// <remarks>
/// Every member is nullable because the projection casts <c>agtype</c> to text, and an agtype
/// null arrives as SQL NULL. Historical edges and vertices frequently omit properties the
/// current write path always supplies.
/// </remarks>
internal sealed record FactRow(
    string? FactId,
    string? Subject,
    string? SubjectType,
    string? Predicate,
    string? Object,
    string? ObjectType,
    string? ScopeId,
    string? Timestamp);

/// <summary>
/// Why a <see cref="FactRow"/> did or did not become a <see cref="KnowledgeFact"/>.
/// </summary>
internal enum FactRowMapOutcome
{
    /// <summary>The row produced a fact.</summary>
    Mapped,

    /// <summary>
    /// The row is missing properties the aggregate requires. These are edges written before
    /// fact metadata was persisted — expected history rather than corruption.
    /// </summary>
    SkippedIncomplete,

    /// <summary>The row supplied values that could not be parsed into the required types.</summary>
    SkippedMalformed
}

/// <summary>
/// Maps a text-projected knowledge graph row onto a <see cref="KnowledgeFact"/>.
/// </summary>
internal static class AgeFactRowMapper
{
    /// <summary>
    /// Entity type substituted when a vertex carries a name but no <c>type</c> property.
    /// </summary>
    /// <remarks>
    /// <see cref="GraphEntity"/> requires a non-empty type, and most stored vertices predate
    /// entity typing. Dropping those rows would discard usable relationships, so the
    /// relationship is kept and the unknown type is made explicit.
    /// </remarks>
    internal const string UnknownEntityType = "Unknown";

    // DateTimeOffset.FromUnixTimeSeconds throws outside this range; a corrupt value must be
    // reported as a skipped row rather than escaping as an exception mid-traversal.
    private const long MinUnixSeconds = -62135596800L; // 0001-01-01T00:00:00Z
    private const long MaxUnixSeconds = 253402300799L; // 9999-12-31T23:59:59Z

    /// <summary>
    /// Attempts to reconstitute a fact from a projected row.
    /// </summary>
    /// <param name="row">The text-projected row.</param>
    /// <param name="fact">The reconstituted fact, or null when the row was skipped.</param>
    /// <returns>The outcome, so callers can distinguish expected history from corruption.</returns>
    internal static FactRowMapOutcome TryMap(FactRow row, out KnowledgeFact? fact)
    {
        ArgumentNullException.ThrowIfNull(row, nameof(row));

        fact = null;

        // Identity and the relationship itself are mandatory; without them there is no aggregate
        // to build. Absent values are legacy rows, not corruption.
        if (string.IsNullOrWhiteSpace(row.FactId) ||
            string.IsNullOrWhiteSpace(row.ScopeId) ||
            string.IsNullOrWhiteSpace(row.Timestamp) ||
            string.IsNullOrWhiteSpace(row.Subject) ||
            string.IsNullOrWhiteSpace(row.Object) ||
            string.IsNullOrWhiteSpace(row.Predicate))
        {
            return FactRowMapOutcome.SkippedIncomplete;
        }

        if (!Guid.TryParse(row.FactId, out var factGuid) ||
            !Guid.TryParse(row.ScopeId, out var scopeGuid))
        {
            return FactRowMapOutcome.SkippedMalformed;
        }

        // AddAsync stores the timestamp as Unix seconds to avoid escaping an ISO 8601 string
        // inside the Cypher literal, so the stored value is a base-10 integer in a string.
        if (!long.TryParse(row.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds) ||
            unixSeconds < MinUnixSeconds ||
            unixSeconds > MaxUnixSeconds)
        {
            return FactRowMapOutcome.SkippedMalformed;
        }

        var subjectType = string.IsNullOrWhiteSpace(row.SubjectType) ? UnknownEntityType : row.SubjectType;
        var objectType = string.IsNullOrWhiteSpace(row.ObjectType) ? UnknownEntityType : row.ObjectType;

        fact = KnowledgeFact.Reconstitute(
            FactId.From(factGuid),
            ScopeId.From(scopeGuid),
            GraphEntity.Create(row.Subject, subjectType),
            row.Predicate.Replace("_", " ", StringComparison.Ordinal),
            GraphEntity.Create(row.Object, objectType),
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

        return FactRowMapOutcome.Mapped;
    }
}
