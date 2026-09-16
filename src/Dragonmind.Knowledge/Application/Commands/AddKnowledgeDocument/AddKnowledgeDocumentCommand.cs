using System.ComponentModel.DataAnnotations;
using Dragonmind.Core.Application;
using Dragonmind.Core.Application.Utilities;
using Dragonmind.Knowledge.Application.DTOs;

namespace Dragonmind.Knowledge.Application.Commands.AddKnowledgeDocument;

/// <summary>
/// Command to add a new knowledge document to the knowledge base.
/// </summary>
public sealed record AddKnowledgeDocumentCommand : ICommand<KnowledgeDocumentDto>
{
    /// <summary>
    /// The scope ID associated with this knowledge document.
    /// </summary>
    [Required]
    [NonEmptyGuid]
    public required Guid ScopeId { get; init; }

    /// <summary>
    /// The content of the knowledge document.
    /// </summary>
    [Required]
    [NonWhitespaceString]
    public required string Content { get; init; }

    /// <summary>
    /// The source of the knowledge document (optional).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// The category of the knowledge document (optional).
    /// </summary>
    public string? Category { get; init; }
}
