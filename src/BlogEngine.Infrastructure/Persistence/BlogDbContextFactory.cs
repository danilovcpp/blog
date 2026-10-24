using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlogEngine.Infrastructure.Persistence;

public sealed class BlogDbContextFactory : IDesignTimeDbContextFactory<BlogDbContext>
{
    public BlogDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Blog");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("Set ConnectionStrings__Blog through the protected migration environment.");
        }
        return new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connection).Options);
    }
}
