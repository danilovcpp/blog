using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Engagement;

public sealed class LikeConfiguration : IEntityTypeConfiguration<Like>
{
    public void Configure(EntityTypeBuilder<Like> b)
    {
        b.ToTable("likes", "engagement");
        b.HasKey(x => new { x.PublicationId, x.UserId }).HasName("pk_likes");
        b.HasOne<Publication>().WithMany().HasForeignKey(x => x.PublicationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_likes_publications");
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_likes_local_users");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_likes_user_id");
        PublishingSql.Columns(b);
    }
}
