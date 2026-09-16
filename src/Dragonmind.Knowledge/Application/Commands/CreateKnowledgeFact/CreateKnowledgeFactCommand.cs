using System.ComponentModel.DataAnnotations;
using Dragonmind.Core.Application;
using Dragonmind.Core.Application.Utilities;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;

namespace Dragonmind.Knowledge.Application.Commands.CreateKnowledgeFact;

/// <summary>
/// Command to create a new knowledge fact in the knowledge graph.
/// </summary>
public sealed record CreateKnowledgeFactCommand : ICommand<KnowledgeFactDto?>
{
    /// <summary>
    /// The scope identifier associated with this knowledge fact.
    /// </summary>
    [Required]
    [NonEmptyGuid]
    public required Guid ScopeId { get; init; }

    /// <summary>
    /// The name of the subject entity in the knowledge fact.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string SubjectName { get; init; }

    /// <summary>
    /// The type of the subject entity in the knowledge fact.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string SubjectType { get; init; }

    /// <summary>
    /// The predicate describing the relationship between subject and object.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string Predicate { get; init; }

    /// <summary>
    /// The name of the object entity in the knowledge fact.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string ObjectName { get; init; }

    /// <summary>
    /// The type of the object entity in the knowledge fact.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string ObjectType { get; init; }
}
