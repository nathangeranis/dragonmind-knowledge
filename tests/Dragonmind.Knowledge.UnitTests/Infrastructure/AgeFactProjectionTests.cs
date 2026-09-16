using System.Text.RegularExpressions;

using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Structural guards on the shared knowledge graph fact projection.
///
/// Two defect classes are pinned here. First, reading an <c>agtype</c> column directly fails at
/// runtime — Npgsql binds unprepared statements requesting the binary result format and the
/// ApacheAGE agtype converter is text-only, which silently disables every graph read. Second, the
/// SELECT list, the cypher column-type list and the Cypher RETURN aliases are positional: when two
/// read methods drift out of order, the mapper reads the wrong columns.
///
/// Both failures are invisible to a compiler, so they are asserted on the constants themselves.
/// </summary>
public class AgeFactProjectionTests
{
    private static readonly string[] ExpectedColumnOrder =
    [
        "fact_id",
        "subject",
        "subject_type",
        "predicate",
        "obj",
        "obj_type",
        "scope_id",
        "timestamp"
    ];

    private static string[] SplitColumns(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void FactColumnsAsText_CastsEveryProjectedColumnToText()
    {
        // A single bare agtype column is enough to throw "Resolved converter does not support
        // Binary format." and take down the whole read.
        var columns = SplitColumns(ApacheAgeKnowledgeGraphRepository.FactColumnsAsText);

        Assert.NotEmpty(columns);
        Assert.All(columns, column =>
            Assert.EndsWith("::text", column, StringComparison.Ordinal));
    }

    [Fact]
    public void FactColumnsAsText_ProjectsTheExpectedColumnsInOrder()
    {
        var columns = SplitColumns(ApacheAgeKnowledgeGraphRepository.FactColumnsAsText)
            .Select(column => column[..column.IndexOf("::", StringComparison.Ordinal)])
            .ToArray();

        Assert.Equal(ExpectedColumnOrder, columns);
    }

    [Fact]
    public void FactColumnTypes_DeclaresTheSameColumnsInTheSameOrder()
    {
        // The cypher() call must declare its output columns as agtype, in projection order.
        var declarations = SplitColumns(ApacheAgeKnowledgeGraphRepository.FactColumnTypes);

        Assert.Equal(ExpectedColumnOrder, declarations.Select(d => d.Split(' ')[0]).ToArray());
        Assert.All(declarations, declaration =>
            Assert.EndsWith(" agtype", declaration, StringComparison.Ordinal));
    }

    [Fact]
    public void FactCypherDistanceAggregation_GroupsByRelationshipAndTakesTheShortestPath()
    {
        // The traversal pattern is undirected and variable-length, so the same edge is reached by
        // paths of several lengths. Putting length(path) inside a DISTINCT key let one edge survive
        // as multiple rows with different distances, and those duplicate facts were rendered
        // verbatim into the downstream prompt. Aggregating with min() is what collapses them.
        var aggregation = ApacheAgeKnowledgeGraphRepository.FactCypherDistanceAggregation;

        Assert.Contains("min(length(path))", aggregation, StringComparison.Ordinal);
        Assert.Contains("WITH rel", aggregation, StringComparison.Ordinal);
        Assert.DoesNotContain("DISTINCT", aggregation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(ApacheAgeKnowledgeGraphRepository.FactCypherProjection))]
    [InlineData(nameof(ApacheAgeKnowledgeGraphRepository.FactCypherProjectionFromRelationship))]
    public void CypherProjections_AliasColumnsInProjectionOrder(string projectionName)
    {
        var projection = projectionName switch
        {
            nameof(ApacheAgeKnowledgeGraphRepository.FactCypherProjection) =>
                ApacheAgeKnowledgeGraphRepository.FactCypherProjection,
            _ => ApacheAgeKnowledgeGraphRepository.FactCypherProjectionFromRelationship
        };

        var aliases = Regex.Matches(projection, @"\bAS\s+(\w+)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(ExpectedColumnOrder, aliases);
    }
}
