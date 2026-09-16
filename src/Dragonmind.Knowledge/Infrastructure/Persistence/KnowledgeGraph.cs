namespace Dragonmind.Knowledge.Infrastructure.Persistence;

/// <summary>
/// The single Apache AGE graph this context reads and writes.
/// </summary>
public static class KnowledgeGraph
{
    /// <summary>
    /// The graph name passed to every <c>cypher()</c> call.
    /// </summary>
    public const string Name = "knowledge_graph";
}
