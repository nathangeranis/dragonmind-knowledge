using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Tests for <see cref="AgeFactRowMapper"/>, which turns a text-projected Apache AGE row into a
/// <see cref="Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate.KnowledgeFact"/>.
///
/// The stored graph can accumulate history: an edge written before fact metadata existed carries
/// no <c>fact_id</c>, and a vertex written before entity typing existed carries no <c>type</c>. The
/// mapper must distinguish rows it simply cannot reconstitute (expected) from rows carrying values
/// it cannot parse (corruption), and must never throw mid-traversal.
/// </summary>
public class AgeFactRowMapperTests
{
    private const string ValidFactId = "06f572a6-d155-36bb-3e21-f66523db4b6f";
    private const string ValidScopeId = "8e8396e4-cfcb-9a5d-517b-b52561a2ecc1";

    // 1786630927 = 2026-08-13T19:22:07Z — the shape AddAsync writes (Unix seconds, as a string).
    private const string ValidTimestamp = "1786630927";

    private static FactRow CompleteRow(
        string? factId = ValidFactId,
        string? subject = "Checkout Worker (1)",
        string? subjectType = "Entity",
        string? predicate = "IS_A",
        string? obj = "Background Job",
        string? objectType = "Entity",
        string? scopeId = ValidScopeId,
        string? timestamp = ValidTimestamp)
        => new(factId, subject, subjectType, predicate, obj, objectType, scopeId, timestamp);

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryMap_WithCompleteRow_MapsEveryFieldToTheCorrectColumn()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(), out var fact);

        Assert.Equal(FactRowMapOutcome.Mapped, outcome);
        Assert.NotNull(fact);
        Assert.Equal(Guid.Parse(ValidFactId), fact!.Id.Value);
        Assert.Equal(Guid.Parse(ValidScopeId), fact.ScopeId.Value);
        Assert.Equal("Checkout Worker (1)", fact.Subject.Name);
        Assert.Equal("Entity", fact.Subject.EntityType);
        Assert.Equal("Background Job", fact.Object.Name);
        Assert.Equal("Entity", fact.Object.EntityType);
    }

    [Fact]
    public void TryMap_WithUnderscoredPredicate_RestoresSpacesForDisplay()
    {
        AgeFactRowMapper.TryMap(CompleteRow(predicate: "REPORTS_TO_ON_CALL"), out var fact);

        Assert.Equal("REPORTS TO ON CALL", fact!.Predicate);
    }

    [Fact]
    public void TryMap_WithUnixSecondsTimestamp_ReconstitutesUtcInstant()
    {
        AgeFactRowMapper.TryMap(CompleteRow(), out var fact);

        Assert.Equal(DateTimeKind.Utc, fact!.Timestamp.Kind);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1786630927).UtcDateTime,
            fact.Timestamp);
    }

    // ── Incomplete rows: legacy graph data, not corruption ──────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryMap_WithoutFactId_ReportsIncomplete(string? factId)
    {
        // Many stored edges predate fact metadata and carry no fact_id.
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(factId: factId), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryMap_WithoutScopeId_ReportsIncomplete(string? scopeId)
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(scopeId: scopeId), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryMap_WithoutTimestamp_ReportsIncomplete(string? timestamp)
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(timestamp: timestamp), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    [Fact]
    public void TryMap_WithoutSubjectName_ReportsIncomplete()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(subject: null), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    [Fact]
    public void TryMap_WithoutObjectName_ReportsIncomplete()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(obj: null), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    [Fact]
    public void TryMap_WithoutPredicate_ReportsIncomplete()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(predicate: null), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedIncomplete, outcome);
        Assert.Null(fact);
    }

    // ── Untyped vertices are kept, not dropped ──────────────────────────────────

    [Fact]
    public void TryMap_WithUntypedVertices_KeepsTheFactAndMarksTypeUnknown()
    {
        // A large share of stored vertices predate entity typing and have a name but no type.
        // GraphEntity demands a non-empty type, so dropping these rows would discard most of the
        // graph's usable relationships.
        var outcome = AgeFactRowMapper.TryMap(
            CompleteRow(subjectType: null, objectType: "   "),
            out var fact);

        Assert.Equal(FactRowMapOutcome.Mapped, outcome);
        Assert.Equal(AgeFactRowMapper.UnknownEntityType, fact!.Subject.EntityType);
        Assert.Equal(AgeFactRowMapper.UnknownEntityType, fact.Object.EntityType);
    }

    // ── Malformed rows: values present but unusable ─────────────────────────────

    [Fact]
    public void TryMap_WithNonGuidFactId_ReportsMalformed()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(factId: "not-a-guid"), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedMalformed, outcome);
        Assert.Null(fact);
    }

    [Fact]
    public void TryMap_WithNonGuidScopeId_ReportsMalformed()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(scopeId: "scope-7"), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedMalformed, outcome);
        Assert.Null(fact);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-08-13T19:22:07Z")]
    [InlineData("1786630927.5")]
    public void TryMap_WithNonNumericTimestamp_ReportsMalformed(string timestamp)
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(timestamp: timestamp), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedMalformed, outcome);
        Assert.Null(fact);
    }

    [Theory]
    [InlineData("253402300800")]        // one second past DateTime.MaxValue
    [InlineData("-62135596801")]        // one second before DateTime.MinValue
    [InlineData("9223372036854775807")] // long.MaxValue
    public void TryMap_WithOutOfRangeTimestamp_ReportsMalformedInsteadOfThrowing(string timestamp)
    {
        // FromUnixTimeSeconds throws outside this range; an exception here would abort the whole
        // traversal over one bad edge.
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(timestamp: timestamp), out var fact);

        Assert.Equal(FactRowMapOutcome.SkippedMalformed, outcome);
        Assert.Null(fact);
    }

    [Fact]
    public void TryMap_WithTimestampAtTheSupportedBoundary_StillMaps()
    {
        var outcome = AgeFactRowMapper.TryMap(CompleteRow(timestamp: "253402300799"), out var fact);

        Assert.Equal(FactRowMapOutcome.Mapped, outcome);
        Assert.NotNull(fact);
    }

    [Fact]
    public void TryMap_WithNullRow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgeFactRowMapper.TryMap(null!, out _));
    }
}
