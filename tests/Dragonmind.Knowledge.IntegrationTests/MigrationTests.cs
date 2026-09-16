using Dragonmind.Knowledge.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Proves the <c>InitialCreate</c> migration actually stands up a working database: both
/// PostgreSQL extensions exist, the Apache AGE graph exists, the pgvector HNSW index exists, and
/// re-running the migration is a no-op rather than an error.
/// </summary>
[Collection("Postgres")]
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private IDbContextFactory<KnowledgeDbContext> _contextFactory = null!;

    public MigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync()
    {
        _contextFactory = _fixture.ContextFactory;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task VectorExtension_Exists()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector';";
            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task AgeExtension_Exists()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'age';";
            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task KnowledgeGraph_ExistsInAgCatalog()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM ag_catalog.ag_graph WHERE name = @name;";
            var nameParam = cmd.CreateParameter();
            nameParam.ParameterName = "name";
            nameParam.Value = KnowledgeGraph.Name;
            cmd.Parameters.Add(nameParam);

            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task VectorHnswIndex_Exists()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pg_indexes WHERE schemaname = 'knowledge' AND indexname = 'ix_documents_vector_hnsw';";
            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task SecondMigrateAsync_IsANoOp()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // The fixture already migrated once during InitializeAsync. A second call must not throw
        // (every statement is IF NOT EXISTS / EnsureSchema-guarded) and must not change whether
        // there are pending migrations.
        await context.Database.MigrateAsync();

        var pending = await context.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);
    }
}
