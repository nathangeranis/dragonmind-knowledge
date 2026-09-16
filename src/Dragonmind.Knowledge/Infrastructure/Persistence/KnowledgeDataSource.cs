using Npgsql;

namespace Dragonmind.Knowledge.Infrastructure.Persistence;

/// <summary>
/// Builds the <see cref="NpgsqlDataSource"/> the Knowledge context's <see cref="KnowledgeDbContext"/>
/// and repositories connect through.
/// </summary>
public static class KnowledgeDataSource
{
    /// <summary>
    /// Creates a data source configured for the Knowledge context's pgvector usage. The host owns
    /// the connection string and the resulting data source's lifetime (typically a singleton
    /// registered before calling <c>AddKnowledgeContext</c>).
    /// </summary>
    /// <param name="connectionString">
    /// The PostgreSQL connection string for the database hosting the <c>knowledge</c> schema.
    /// </param>
    public static NpgsqlDataSource Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }
}
