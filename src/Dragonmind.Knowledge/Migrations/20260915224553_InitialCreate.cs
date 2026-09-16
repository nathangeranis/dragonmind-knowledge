using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Dragonmind.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    vector = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_scope_id",
                schema: "knowledge",
                table: "documents",
                column: "scope_id");

            // Apache AGE and the HNSW vector index are hand-written SQL because EF Core's model
            // builder has no concept of either: AGE ships as a Postgres extension that must be
            // created before ag_catalog.create_graph can run, and pgvector's HNSW index type
            // (with its m/ef_construction tuning parameters) isn't one of the index kinds the
            // relational model can express via HasIndex. Both statements are autocommitted
            // (suppressTransaction: true) because CREATE EXTENSION and ag_catalog.create_graph
            // are not transaction-safe operations in Postgres/AGE, and both are schema-qualified
            // (ag_catalog / knowledge.documents) so they don't depend on search_path.
            migrationBuilder.Sql(
                "CREATE EXTENSION IF NOT EXISTS age;",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "SELECT ag_catalog.create_graph('knowledge_graph') " +
                "WHERE NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_documents_vector_hnsw ON knowledge.documents " +
                "USING hnsw (vector vector_cosine_ops) WITH (m = 16, ef_construction = 64);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS knowledge.ix_documents_vector_hnsw;");

            migrationBuilder.Sql(
                "SELECT ag_catalog.drop_graph('knowledge_graph', true) " +
                "WHERE EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph');",
                suppressTransaction: true);

            migrationBuilder.DropTable(
                name: "documents",
                schema: "knowledge");
        }
    }
}
