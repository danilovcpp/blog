using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence;

// PostgreSQL wall time, sampled after lock acquisition. Tests replace this boundary, never the dedup store.
public class DatabaseClock
{
    public virtual Task<DateTimeOffset> ReadAsync(BlogDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
}
