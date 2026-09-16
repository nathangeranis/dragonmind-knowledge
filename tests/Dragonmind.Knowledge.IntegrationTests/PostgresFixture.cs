using Dragonmind.Core.AI;
using Dragonmind.Knowledge.Infrastructure.DI;
using Dragonmind.Knowledge.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// Generates a fixed 1536-dimension vector for every input. Only <see cref="AddKnowledgeContext"/>'s
/// DI graph requires an <see cref="IEmbeddingGenerator"/> to be registered — none of the integration
/// tests in this project call it, so the actual values never matter.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[EmbeddingDimensions.Default]);
}

/// <summary>
/// Collection fixture wiring a single real PostgreSQL + pgvector + Apache AGE instance for every
/// integration test class in this project.
/// <para>
/// The connection string comes from <c>KNOWLEDGE_TEST_CONNECTION</c> exclusively. There is no
/// fallback and no skip path: an integration test that silently no-ops when the database is
/// unreachable is worse than one that fails loudly, because a green run then proves nothing.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringEnvironmentVariable = "KNOWLEDGE_TEST_CONNECTION";

    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// The data source every repository and the <see cref="KnowledgeDbContext"/> factory connect
    /// through. Built once for the lifetime of the fixture via <see cref="KnowledgeDataSource.Create"/>.
    /// </summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>
    /// The pooled <see cref="KnowledgeDbContext"/> factory, built exactly the way a host application
    /// wires it via <see cref="AddKnowledgeContext"/>.
    /// </summary>
    public IDbContextFactory<KnowledgeDbContext> ContextFactory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The {ConnectionStringEnvironmentVariable} environment variable is not set. " +
                "Integration tests require a real PostgreSQL instance (see docker-compose.yml) — " +
                "there is no in-memory fallback and no skip path.");
        }

        DataSource = KnowledgeDataSource.Create(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IEmbeddingGenerator, FakeEmbeddingGenerator>();
        services.AddKnowledgeContext(DataSource);

        _serviceProvider = services.BuildServiceProvider();
        ContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        await using var context = await ContextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        await DataSource.DisposeAsync();
    }
}

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one migrated database, one connection pool) across
/// every test class in this collection.
/// </summary>
[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
