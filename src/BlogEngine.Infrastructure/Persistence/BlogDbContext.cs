using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Infrastructure.Persistence.Accounts;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence;

public sealed class BlogDbContext(DbContextOptions<BlogDbContext> options) : DbContext(options)
{
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LocalUserConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration());
    }
}
