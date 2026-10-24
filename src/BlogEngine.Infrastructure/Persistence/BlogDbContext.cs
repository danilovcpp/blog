using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Infrastructure.Persistence.Engagement;
using BlogEngine.Infrastructure.Persistence.Publishing;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence;

public sealed class BlogDbContext(DbContextOptions<BlogDbContext> options) : DbContext(options)
{
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<RoleChangeAudit> RoleChangeAudits => Set<RoleChangeAudit>();
    public DbSet<Publication> Publications => Set<Publication>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PublicationCategory> PublicationCategories => Set<PublicationCategory>();
    public DbSet<PublicationTag> PublicationTags => Set<PublicationTag>();
    public DbSet<Like> Likes => Set<Like>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LocalUserConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new RoleChangeAuditConfiguration());
        modelBuilder.ApplyConfiguration(new PublicationConfiguration());
        modelBuilder.ApplyConfiguration<Category>(new TaxonomyConfiguration());
        modelBuilder.ApplyConfiguration<Tag>(new TaxonomyConfiguration());
        modelBuilder.ApplyConfiguration<PublicationCategory>(new ClassificationConfiguration());
        modelBuilder.ApplyConfiguration<PublicationTag>(new ClassificationConfiguration());
        modelBuilder.ApplyConfiguration(new LikeConfiguration());
    }
}
