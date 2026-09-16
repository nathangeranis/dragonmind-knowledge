namespace Dragonmind.Core.Domain;

/// <summary>
/// Interface for the Unit of Work pattern.
/// </summary>
public interface IUnitOfWork
{
    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}
