using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Engagement;

public sealed class ViewDeduplicationConfiguration : IEntityTypeConfiguration<ViewDeduplicationRecord>
{
    public void Configure(EntityTypeBuilder<ViewDeduplicationRecord> b)
    {
        b.ToTable("view_deduplication_records", "engagement", table =>
        {
            table.HasCheckConstraint("ck_view_deduplication_kind", "visitor_kind IN (1,2)");
            table.HasCheckConstraint("ck_view_deduplication_key", "octet_length(visitor_key) = 32");
        });
        b.HasKey(x => new { x.PublicationId, x.VisitorKind, x.VisitorKey }).HasName("pk_view_deduplication_records");
        b.HasOne<Publication>().WithMany().HasForeignKey(x => x.PublicationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_view_deduplication_publications");
        b.HasIndex(x => new { x.LastCountedAt, x.PublicationId }).HasDatabaseName("ix_view_deduplication_expiry");
        PersistenceSql.Columns(b);
    }
}
