namespace Dragonmind.Core.Domain.SharedIdentities;

/// <summary>
/// Strongly-typed identifier for the scope (agent session, tenant, or workspace) a piece of
/// knowledge belongs to.
/// </summary>
public sealed class ScopeId : StronglyTypedId<ScopeId>
{
    private ScopeId(Guid value) : base(value)
    {
    }

    /// <summary>
    /// Creates a ScopeId from an existing Guid value.
    /// </summary>
    public static ScopeId From(Guid value) => CreateFrom(value);

    /// <summary>
    /// Creates a new ScopeId with a new Guid value.
    /// </summary>
    public static ScopeId New() => NewId();
}
