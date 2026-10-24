using BlogEngine.Core.Publishing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class ClassificationConfiguration : IEntityTypeConfiguration<PublicationCategory>, IEntityTypeConfiguration<PublicationTag>
{
    public void Configure(EntityTypeBuilder<PublicationCategory> b)
    {
        b.ToTable("publication_categories", "publishing");
        b.HasKey(x => new { x.PublicationId, x.CategoryId }).HasName("pk_publication_categories");
        b.HasOne<Publication>().WithMany().HasForeignKey(x => x.PublicationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_publication_categories_publications");
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_publication_categories_categories");
        b.HasIndex(x => new { x.CategoryId, x.PublicationId }).HasDatabaseName("ix_publication_categories_reverse");
        PersistenceSql.Columns(b);
    }
    public void Configure(EntityTypeBuilder<PublicationTag> b)
    {
        b.ToTable("publication_tags", "publishing");
        b.HasKey(x => new { x.PublicationId, x.TagId }).HasName("pk_publication_tags");
        b.HasOne<Publication>().WithMany().HasForeignKey(x => x.PublicationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_publication_tags_publications");
        b.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_publication_tags_tags");
        b.HasIndex(x => new { x.TagId, x.PublicationId }).HasDatabaseName("ix_publication_tags_reverse");
        PersistenceSql.Columns(b);
    }
}
