using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using EFCore.NamingConventions;

using MediatR;

using Npgsql;

using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Application.Behaviors;
using Dragonmind.Core.Application.Caching;
using Dragonmind.Core.Infrastructure.Caching;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;
using Dragonmind.Knowledge.Infrastructure.Caching;
using Dragonmind.Knowledge.Infrastructure.DomainServices;
using Dragonmind.Knowledge.Infrastructure.Embeddings;
using Dragonmind.Knowledge.Infrastructure.Persistence;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

namespace Dragonmind.Knowledge.Infrastructure.DI;

/// <summary>
/// Wires the Knowledge bounded context into a host's dependency injection container.
/// </summary>
public static class KnowledgeContextServiceExtensions
{
    /// <summary>
    /// Registers every Knowledge context service: the pooled <see cref="KnowledgeDbContext"/>
    /// factory, repositories, domain services, caching decorators, MediatR (scanning this
    /// assembly for command/query handlers and wiring the shared validation and logging pipeline
    /// behaviors), and the <see cref="IKnowledgeContextFacade"/> — resolved as a caching decorator
    /// wrapping the concrete facade, which is the only member of the context other bounded
    /// contexts (in a host application) may depend on.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="dataSource">
    /// A pre-built <see cref="NpgsqlDataSource"/> for the database hosting the <c>knowledge</c>
    /// schema — see <see cref="KnowledgeDataSource.Create"/>. The host owns its lifetime.
    /// </param>
    /// <param name="configure">
    /// Optional callback to set <see cref="KnowledgeOptions"/> (currently just the embedding
    /// model name used in the embedding cache key). Left unset, the options default applies.
    /// </param>
    /// <remarks>
    /// The host must separately register:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> — e.g.
    /// <c>AddDistributedMemoryCache()</c> for local development/tests, or a Redis-backed
    /// implementation for production. This context only registers <see cref="ICacheService"/>
    /// (via <see cref="TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>, so a
    /// host-supplied <see cref="ICacheService"/> registered before this call wins instead) as a
    /// thin decorator over whatever <c>IDistributedCache</c> the host provides.
    /// </description></item>
    /// <item><description>
    /// <see cref="Dragonmind.Core.AI.IEmbeddingGenerator"/> — a concrete embedding provider (see
    /// <see cref="Dragonmind.Core.AI.ExtensionsAIEmbeddingGenerator"/>). This context does not
    /// ship one, so vector search and document storage cannot function without it.
    /// </description></item>
    /// </list>
    /// Command and query handlers are never registered individually here — MediatR's assembly
    /// scan discovers every <c>ICommandHandler</c>/<c>IQueryHandler</c> implementation in this
    /// assembly automatically.
    /// </remarks>
    public static IServiceCollection AddKnowledgeContext(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Action<KnowledgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.Configure<KnowledgeOptions>(options => configure?.Invoke(options));

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(KnowledgeContextServiceExtensions).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationPipelineBehavior<,>));
            cfg.AddOpenBehavior(typeof(LoggingPipelineBehavior<,>));
        });

        services.AddPooledDbContextFactory<KnowledgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsqlOptions =>
                {
                    npgsqlOptions.UseVector();
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "knowledge");
                })
                .UseSnakeCaseNamingConvention());

        // Repositories
        services.AddScoped<IKnowledgeDocumentRepository, EfCoreKnowledgeDocumentRepository>();
        services.AddScoped<IKnowledgeGraphRepository, ApacheAgeKnowledgeGraphRepository>();

        // Domain services
        services.AddScoped<IVectorSearchService, VectorSearchService>();
        services.AddScoped<IGraphTraversalService, GraphTraversalService>();

        // Cache services
        services.AddScoped<IKnowledgeCacheService, KnowledgeCacheService>();
        services.AddScoped<IEmbeddingCacheService, EmbeddingCacheService>();

        // The host may already have registered a general-purpose ICacheService (e.g. sharing one
        // across several contexts); only supply the default IDistributedCache-backed one if not.
        services.TryAddSingleton<ICacheService, DistributedCacheService>();

        // Embedding generation delegates to the host-supplied IEmbeddingGenerator, decorated with
        // a cache-aside layer keyed on (text, KnowledgeOptions.EmbeddingModelName) so identical
        // embeddings are served from cache instead of a live provider call on every request.
        services.AddScoped<IEmbeddingService>(provider =>
        {
            var inner = new EmbeddingService(provider.GetRequiredService<Dragonmind.Core.AI.IEmbeddingGenerator>());
            var modelName = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value.EmbeddingModelName;

            return new CachingEmbeddingService(
                inner,
                provider.GetRequiredService<IEmbeddingCacheService>(),
                modelName);
        });

        // Anti-Corruption Layer: register the concrete facade, then decorate it with caching.
        // IKnowledgeContextFacade always resolves to the caching decorator — the concrete facade
        // is never exposed directly to callers.
        services.AddScoped<KnowledgeContextFacade>();
        services.AddScoped<IKnowledgeContextFacade>(provider =>
        {
            var innerFacade = provider.GetRequiredService<KnowledgeContextFacade>();
            var cacheService = provider.GetRequiredService<IKnowledgeCacheService>();
            var logger = provider.GetRequiredService<ILogger<CachingKnowledgeContextFacade>>();
            return new CachingKnowledgeContextFacade(innerFacade, cacheService, logger);
        });

        return services;
    }
}
