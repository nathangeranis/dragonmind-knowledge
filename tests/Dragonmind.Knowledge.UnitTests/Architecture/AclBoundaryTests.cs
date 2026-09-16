using System.Reflection;

using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Domain;

namespace Dragonmind.Knowledge.UnitTests.Architecture;

/// <summary>
/// Enforces the anti-corruption boundary at the assembly/reflection level, so a future change
/// can't quietly reintroduce a dependency these tests are designed to catch:
/// <list type="bullet">
/// <item>Foundation (<c>Dragonmind.Core</c>) never references the Knowledge context — the
/// dependency only ever flows the other way.</item>
/// <item>The facade contract (<see cref="IKnowledgeContextFacade"/>) exposes nothing but
/// Foundation and framework (BCL) types — a caller across the boundary never needs to know about
/// a Knowledge-context domain or infrastructure type to use it.</item>
/// <item>The Knowledge domain's public signatures never mention a persistence type (EF Core,
/// Npgsql, Pgvector) — domain purity by construction, not just by convention.</item>
/// </list>
/// These assertions must never be weakened to make a change pass; a failure here means a real
/// boundary leak in the source, not a test to relax.
/// </summary>
public class AclBoundaryTests
{
    /// <summary>
    /// Expands a type into every type reachable from it: array element types, and — for a
    /// generic type — the open generic definition plus each type argument, recursively. This is
    /// what lets the tests below catch a persistence type hiding inside
    /// <c>Task&lt;IReadOnlyList&lt;SomeEfEntity&gt;&gt;</c> rather than only checking the
    /// outermost <c>Task</c>.
    /// </summary>
    private static IEnumerable<Type> ExpandType(Type type)
    {
        if (type.IsByRef || type.IsPointer)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var t in ExpandType(element))
                {
                    yield return t;
                }
            }
            yield break;
        }

        if (type.IsArray)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var t in ExpandType(element))
                {
                    yield return t;
                }
            }
            yield break;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var t in ExpandType(argument))
                {
                    yield return t;
                }
            }
            yield break;
        }

        yield return type;
    }

    private static bool NamespaceStartsWith(Type type, string prefix)
        => (type.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal);

    [Fact]
    public void Foundation_NeverReferencesTheKnowledgeContext()
    {
        // Foundation is the dependency-free base every context builds on: the relationship is
        // one-directional by design (Dragonmind.Core.csproj carries zero ProjectReferences). An
        // assembly reference to the Knowledge context — direct or introduced by a future
        // refactor — would invert that and is exactly what this guards against.
        var foundationAssembly = typeof(ValueObject).Assembly;

        var referencedAssemblyNames = foundationAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToList();

        Assert.DoesNotContain(
            referencedAssemblyNames,
            name => name.Contains("Knowledge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FacadeContract_ExposesOnlyFoundationAndFrameworkTypes()
    {
        // A caller on the other side of the ACL boundary should never need a `using` for a
        // Knowledge-context domain or infrastructure namespace just to call the facade: every
        // type reachable from its methods must resolve to either the BCL/a framework namespace
        // (System.*, MediatR, etc.) or Foundation itself (Dragonmind.Core.*).
        var facadeType = typeof(IKnowledgeContextFacade);

        var reachableTypes = facadeType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method =>
                method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType))
            .SelectMany(ExpandType)
            .Distinct()
            .ToList();

        var violations = reachableTypes
            .Where(t => !NamespaceStartsWith(t, "System")
                     && !NamespaceStartsWith(t, "Dragonmind.Core")
                     && !NamespaceStartsWith(t, "MediatR"))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "IKnowledgeContextFacade exposes non-Foundation, non-framework type(s): "
                + string.Join(", ", violations.Select(t => t.FullName)));
    }

    [Fact]
    public void Domain_SignaturesNeverMentionPersistenceTypes()
    {
        // Domain and Application/Infrastructure share one assembly in this repo, so this test
        // can't rely on a project boundary to keep the domain pure — it has to check the public
        // signatures of every type under the Domain namespace directly. A persistence type
        // (EF Core, Npgsql, Pgvector) appearing there would mean the domain layer had stopped
        // being infrastructure-independent.
        var domainAssembly = typeof(Dragonmind.Knowledge.Domain.ValueObjects.RelationshipTypes).Assembly;
        var forbiddenPrefixes = new[] { "Microsoft.EntityFrameworkCore", "Npgsql", "Pgvector" };

        var domainTypes = domainAssembly
            .GetTypes()
            .Where(t => t.IsPublic && (t.Namespace ?? string.Empty).StartsWith("Dragonmind.Knowledge.Domain", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(domainTypes);

        var violations = new List<string>();

        foreach (var type in domainTypes)
        {
            var memberTypes = new List<(string Member, Type Type)>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                memberTypes.Add(($"{type.FullName}.{property.Name}", property.PropertyType));
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    memberTypes.Add(($"{type.FullName}(ctor)", parameter.ParameterType));
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    // Skips property accessors (get_/set_) and operator overloads, which are
                    // already covered via GetProperties above or aren't part of the API surface
                    // this test is checking.
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    memberTypes.Add(($"{type.FullName}.{method.Name}", parameter.ParameterType));
                }

                memberTypes.Add(($"{type.FullName}.{method.Name}", method.ReturnType));
            }

            foreach (var (member, memberType) in memberTypes)
            {
                foreach (var reachable in ExpandType(memberType))
                {
                    var ns = reachable.Namespace ?? string.Empty;
                    if (forbiddenPrefixes.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        violations.Add($"{member} mentions persistence type {reachable.FullName}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Domain signature(s) mention persistence types: " + string.Join("; ", violations));
    }
}
