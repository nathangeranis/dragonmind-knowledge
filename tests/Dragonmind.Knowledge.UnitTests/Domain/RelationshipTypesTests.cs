using System.Text.RegularExpressions;

using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Domain;

/// <summary>
/// Pins the closed relationship-type vocabulary (<see cref="RelationshipTypes.Allowed"/>) and the
/// injection-safety guarantee it exists to provide: every entry is a bare uppercase identifier,
/// because Apache AGE cannot parameterize a relationship type and each one is interpolated into
/// Cypher unquoted.
/// </summary>
public class RelationshipTypesTests
{
    /// <summary>
    /// Thrown by the mock factory when AddAsync reaches the database boundary. For an ACCEPTED
    /// predicate that is the success signal, not a failure: it proves validation ran, passed,
    /// and execution continued. A rejected predicate throws ArgumentException earlier and never
    /// gets here, so there is no live database in either path.
    /// </summary>
    private const string ReachedDatabaseBoundary = "Reached the database boundary (validation passed).";

    private static ApacheAgeKnowledgeGraphRepository CreateRepository()
    {
        var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(ReachedDatabaseBoundary));
        return new ApacheAgeKnowledgeGraphRepository(
            mockFactory.Object,
            NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);
    }

    /// <summary>
    /// Every entry the allowlist accepts must actually reach the repository's write path: a
    /// vocabulary the graph never honours is a defect this test would immediately expose.
    /// </summary>
    [Fact]
    public async Task EveryAllowedType_IsAcceptedByTheGraph()
    {
        var repo = CreateRepository();
        var rejected = new List<string>();

        foreach (var type in RelationshipTypes.Allowed)
        {
            var fact = KnowledgeFact.Create(
                ScopeId.New(),
                GraphEntity.Create("Checkout Service", "Service"),
                type,
                GraphEntity.Create("Payments DB", "Service"));

            var ex = await Record.ExceptionAsync(() => repo.AddAsync(fact));

            // Require the sentinel rather than merely "not an ArgumentException". Accepting
            // anything else would let three different failures pass silently: an allowlist
            // rejection (ArgumentException), an unexpected fault somewhere in validation, and
            // AddAsync returning without ever reaching the database at all (no exception).
            if (ex is not InvalidOperationException { Message: ReachedDatabaseBoundary })
            {
                rejected.Add($"{type} -> {ex?.GetType().Name ?? "no exception"}: {ex?.Message ?? "(none)"}");
            }
        }

        Assert.True(
            rejected.Count == 0,
            "Every allowed type must pass validation and reach the database boundary. Failures:"
                + Environment.NewLine + string.Join(Environment.NewLine, rejected));
    }

    /// <summary>
    /// The allowlist is the ONLY barrier for relationship labels: they are interpolated into
    /// Cypher unquoted because AGE cannot parameterize a relationship type. A careless future
    /// entry containing quotes, brackets or whitespace would be an injection vector.
    /// </summary>
    [Fact]
    public void EveryAllowedType_IsASafeCypherLabel()
    {
        // \A and \z, not ^ and $: in .NET, $ also matches immediately BEFORE a trailing newline,
        // so "IS_A\n" would satisfy ^[A-Z][A-Z0-9_]*$ and slip a newline into an unquoted
        // Cypher label. A guard on an injection boundary must anchor to the absolute string end.
        var unsafeLabels = RelationshipTypes.Allowed
            .Where(t => !Regex.IsMatch(t, @"\A[A-Z][A-Z0-9_]*\z"))
            .ToList();

        Assert.True(
            unsafeLabels.Count == 0,
            $"Relationship labels are interpolated into Cypher unquoted; these are not safe identifiers: {string.Join(", ", unsafeLabels)}");
    }

    /// <summary>
    /// Pins the vocabulary from OUTSIDE the collection under test. A test that merely iterates
    /// <see cref="RelationshipTypes.Allowed"/> passes vacuously if the vocabulary shrinks; only a
    /// literal list catches a dropped entry or a typo.
    /// </summary>
    [Fact]
    public void Allowed_IsExactlyTheExpectedVocabulary()
    {
        string[] expected =
        {
            "IS_A", "PART_OF", "LOCATED_IN", "DEPENDS_ON", "OWNS", "CREATED_BY",
            "MEMBER_OF", "RELATED_TO", "REFERENCES", "CONTAINS", "REPLACES", "KNOWS"
        };

        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RelationshipTypes.Allowed.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The allowlist is a security boundary; exposing it as IReadOnlySet must not let a caller
    /// cast it back to a mutable set and add a label at runtime.
    /// </summary>
    [Fact]
    public void Allowed_CannotBeMutatedThroughADowncast()
    {
        Assert.False(RelationshipTypes.Allowed is HashSet<string>);

        if (RelationshipTypes.Allowed is ISet<string> mutable)
        {
            Assert.Throws<NotSupportedException>(() => mutable.Add("PWNED"));
        }

        Assert.False(RelationshipTypes.IsAllowed("PWNED"));
    }

    [Theory]
    [InlineData("is_a", "IS_A")]
    [InlineData("located in", "LOCATED_IN")]
    [InlineData("  Depends On  ", "DEPENDS_ON")]
    public void Normalize_ConvertsToCanonicalForm(string input, string expected)
    {
        Assert.Equal(expected, RelationshipTypes.Normalize(input));
    }

    [Theory]
    [InlineData("IS_A")]
    [InlineData("is_a")]
    [InlineData("located in")]
    public void IsAllowed_AcceptsKnownTypesInAnyCasing(string predicate)
    {
        Assert.True(RelationshipTypes.IsAllowed(predicate));
    }

    [Theory]
    [InlineData("SIBLING_OF")]
    [InlineData("DETACH DELETE")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_RejectsUnknownOrMalformedTypes(string? predicate)
    {
        Assert.False(RelationshipTypes.IsAllowed(predicate));
    }
}
