using Dragonmind.Core.AI;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Pgvector;

namespace Dragonmind.Knowledge.Infrastructure.Persistence.EntityConfigurations;

/// <summary>
/// Entity configuration for KnowledgeDocument aggregate.
/// Maps the domain entity to PostgreSQL with pgvector support.
/// </summary>
public class KnowledgeDocumentEntityConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("documents");

        // Primary key - map strongly-typed ID
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasConversion(
                id => id.Value,
                value => DocumentId.From(value))
            .HasColumnName("id");

        // ScopeId - map strongly-typed ID
        builder.Property(e => e.ScopeId)
            .HasConversion(
                id => id.Value,
                value => ScopeId.From(value))
            .HasColumnName("scope_id")
            .IsRequired();

        // Timestamp
        builder.Property(e => e.Timestamp)
            .HasColumnName("timestamp")
            .IsRequired();

        // DocumentContent value object - owned entity
        builder.OwnsOne(e => e.Content, content =>
        {
            content.Property(c => c.Value)
                .HasColumnName("content")
                .HasColumnType("text")
                .IsRequired(false);

            content.Property(c => c.Source)
                .HasColumnName("source")
                .HasColumnType("text");

            content.Property(c => c.Category)
                .HasColumnName("category")
                .HasColumnType("text");
        });

        // Embedding value object - complex mapping with pgvector
        // We need to store the float[] as a Vector column
        builder.OwnsOne(e => e.Embedding, embedding =>
        {
            var vectorProperty = embedding.Property(e => e.Vector)
                .HasColumnName("vector")
                .HasColumnType($"vector({EmbeddingDimensions.Default})")
                .HasConversion(
                    vector => new Vector(vector),
                    vector => vector.ToArray());

            // Configure value comparer for change tracking. float[] is mutable and the getter is an
            // auto-property handing back the same array every read, so EF's reference-based default
            // would compare the snapshot against itself and miss an element-level edit. Defensive
            // today: Embedding is immutable and KnowledgeDocument replaces the whole value object.
            // On cost: the list reads (GetByIdAsync, GetByScopeIdAsync) are AsNoTracking and so take
            // no snapshot at all. SearchBySimilarityAsync is the exception — it loads each hit
            // through FindAsync, which tracks — so a RAG query does pay one embedding-width-float
            // copy per matched document. That set is bounded to maxResults (5 by default), so it is
            // a handful of arrays per query, not per row in the table.
            vectorProperty.Metadata.SetValueComparer(
                new ValueComparer<float[]>(
                    (v1, v2) => (v1 == null && v2 == null) || (v1 != null && v2 != null && v1.SequenceEqual(v2)),
                    v => v == null ? 0 : v.Aggregate(0, (a, f) => HashCode.Combine(a, f)),
                    v => v == null ? Array.Empty<float>() : v.ToArray()));
        });

        // Create index on scope_id for efficient scope lookups
        builder.HasIndex(e => e.ScopeId)
            .HasDatabaseName("ix_documents_scope_id");
    }
}
