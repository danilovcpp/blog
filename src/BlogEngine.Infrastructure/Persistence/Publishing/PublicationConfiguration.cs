using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Publishing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

internal static class PublishingSql
{
    public const string SlugCheck = "char_length(slug) BETWEEN 1 AND 120 AND slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'";
}

public sealed class PublicationConfiguration : IEntityTypeConfiguration<Publication>
{
    public void Configure(EntityTypeBuilder<Publication> b)
    {
        b.ToTable("publications", "publishing", table =>
        {
            table.HasCheckConstraint("ck_publications_title", $"title = {PersistenceSql.Trim("title")} AND char_length(title) BETWEEN 1 AND 200");
            table.HasCheckConstraint("ck_publications_slug", PublishingSql.SlugCheck);
            table.HasCheckConstraint("ck_publications_markdown", "octet_length(markdown) <= 1048576");
            table.HasCheckConstraint("ck_publications_description", "description IS NULL OR char_length(description) <= 500");
            table.HasCheckConstraint("ck_publications_cover_url", "cover_url IS NULL OR (char_length(cover_url) BETWEEN 1 AND 2048 AND cover_url ~* '^https://')");
            table.HasCheckConstraint("ck_publications_state", "state IN (0, 1)");
            table.HasCheckConstraint("ck_publications_published", $"state <> 1 OR (first_published_at IS NOT NULL AND char_length({PersistenceSql.Trim("markdown")}) > 0)");
            table.HasCheckConstraint("ck_publications_counters", "like_count >= 0 AND view_count >= 0");
        });
        b.HasKey(p => p.Id).HasName("pk_publications");
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.Title).IsRequired(); b.Property(p => p.Slug).IsRequired(); b.Property(p => p.Markdown).IsRequired();
        b.Property(p => p.State).HasConversion<short>();
        b.Property(p => p.Version).IsConcurrencyToken().ValueGeneratedNever();
        b.Property(p => p.LikeCount).HasDefaultValue(0L); b.Property(p => p.ViewCount).HasDefaultValue(0L);
        b.HasIndex(p => p.Slug).IsUnique().HasDatabaseName("uq_publications_slug");
        b.HasOne<LocalUser>().WithMany().HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_publications_local_users");
        b.HasIndex(p => new { p.FirstPublishedAt, p.Id }).IsDescending().HasFilter("state = 1 AND deleted_at IS NULL").HasDatabaseName("ix_publications_recent");
        b.HasIndex(p => new { p.LikeCount, p.FirstPublishedAt, p.Id }).IsDescending().HasFilter("state = 1 AND deleted_at IS NULL").HasDatabaseName("ix_publications_popular");
        b.HasIndex(p => new { p.AuthorId, p.UpdatedAt, p.Id }).IsDescending(false, true, true).HasFilter("deleted_at IS NULL").HasDatabaseName("ix_publications_workspace_author");
        b.HasIndex(p => new { p.UpdatedAt, p.Id }).IsDescending().HasFilter("deleted_at IS NULL").HasDatabaseName("ix_publications_workspace_admin");
        PersistenceSql.Columns(b);
    }
}
