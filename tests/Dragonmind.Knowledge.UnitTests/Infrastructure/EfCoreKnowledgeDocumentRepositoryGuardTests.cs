using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Guard/validation tests for <see cref="EfCoreKnowledgeDocumentRepository"/>.
/// Tests input validation guards that fire before any database access.
///
/// Note: CRUD operations (GetByIdAsync, GetByScopeIdAsync, AddAsync, UpdateAsync, DeleteAsync)
/// and SearchBySimilarityAsync use EF Core and a raw NpgsqlCommand respectively, which require a
/// real PostgreSQL+pgvector instance to test meaningfully. Those tests belong in
/// tests/Dragonmind.Knowledge.IntegrationTests, against a real database.
///
/// The repository correctly uses pgvector's native &lt;=&gt; cosine distance operator with
/// server-side WHERE/ORDER BY/LIMIT — it does NOT load all documents into memory.
/// </summary>
public class EfCoreKnowledgeDocumentRepositoryGuardTests
{
    private readonly Mock<IDbContextFactory<KnowledgeDbContext>> _mockContextFactory;
    private readonly EfCoreKnowledgeDocumentRepository _sut;

    public EfCoreKnowledgeDocumentRepositoryGuardTests()
    {
        _mockContextFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>();
        _sut = new EfCoreKnowledgeDocumentRepository(_mockContextFactory.Object);
    }

    [Fact]
    public async Task AddAsync_NullDocument_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.AddAsync(null!));

        // Verify factory was never called — guard fires first
        _mockContextFactory.Verify(
            x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_NullDocument_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.UpdateAsync(null!));

        // Verify factory was never called — guard fires first
        _mockContextFactory.Verify(
            x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchBySimilarityAsync_NullQueryVector_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.SearchBySimilarityAsync(null!));

        // Verify factory was never called — guard fires first
        _mockContextFactory.Verify(
            x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
