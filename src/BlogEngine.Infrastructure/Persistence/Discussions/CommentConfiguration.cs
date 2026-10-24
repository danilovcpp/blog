using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Publishing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Discussions;

public sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.ToTable("comments", "discussions", table =>
        {
            table.HasCheckConstraint("ck_comments_state", "state IN (0,1,2)");
            table.HasCheckConstraint("ck_comments_depth", "depth BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_comments_count", "visible_descendant_count >= 0");
            table.HasCheckConstraint("ck_comments_root", "(parent_id IS NULL) = (depth = 1)");
            table.HasCheckConstraint("ck_comments_self_parent", "parent_id IS NULL OR parent_id <> id");
            table.HasCheckConstraint("ck_comments_text", $"(state = 2 AND text IS NULL) OR (state IN (0,1) AND text IS NOT NULL AND text = {PersistenceSql.Trim("text")} AND char_length(text) BETWEEN 1 AND 5000)");
        });
        b.HasKey(c => c.Id).HasName("pk_comments"); b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.State).HasConversion<short>(); b.Property(c => c.Version).IsConcurrencyToken().ValueGeneratedNever();
        b.Property(c => c.VisibleDescendantCount).HasDefaultValue(0L);
        b.HasAlternateKey(c => new { c.PublicationId, c.Id }).HasName("uq_comments_publication_id");
        b.HasOne<Comment>().WithMany().HasForeignKey(c => new { c.PublicationId, c.ParentId }).HasPrincipalKey(c => new { c.PublicationId, c.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_parent");
        b.HasOne<Publication>().WithMany().HasForeignKey(c => c.PublicationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_publication");
        b.HasOne<LocalUser>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_author");
        b.HasIndex(c => c.AuthorId).HasDatabaseName("ix_comments_author");
        b.HasIndex(c => new { c.PublicationId, c.ParentId, c.CreatedAt, c.Id }).HasFilter("state = 0 OR visible_descendant_count > 0").HasDatabaseName("ix_comments_public_parent");
        b.HasIndex(c => new { c.PublicationId, c.ParentId }).HasDatabaseName("ix_comments_parent");
        b.HasIndex(c => new { c.CreatedAt, c.Id }).IsDescending().HasDatabaseName("ix_comments_moderation");
        b.HasIndex(c => new { c.PublicationId, c.State, c.CreatedAt, c.Id }).IsDescending(false, false, true, true).HasDatabaseName("ix_comments_publication_state");
        PersistenceSql.Columns(b);
    }
}
