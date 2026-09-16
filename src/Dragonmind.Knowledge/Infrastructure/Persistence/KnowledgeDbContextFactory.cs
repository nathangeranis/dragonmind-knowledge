using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dragonmind.Knowledge.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations in the Knowledge context.
/// </summary>
public class KnowledgeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("KNOWLEDGE_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "The KNOWLEDGE_DB_CONNECTION environment variable must be set to run migrations.");

        var optionsBuilder = new DbContextOptionsBuilder<KnowledgeDbContext>();

        optionsBuilder.UseNpgsql(connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "knowledge");
            })
            .UseSnakeCaseNamingConvention();

        return new KnowledgeDbContext(optionsBuilder.Options);
    }
}
