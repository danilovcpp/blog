using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.Infrastructure.Persistence;

public static class DatabaseErrorMapper
{
    public static DomainError? Map(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException) { return new DomainError("concurrency_conflict"); }
        var cause = exception;
        // EF's execution strategy wraps transient provider errors in InvalidOperationException.
        while (cause is not NpgsqlException && cause.InnerException is { } inner) { cause = inner; }
        // Do not infer business conflicts from arbitrary SQLSTATEs; add each named constraint with its owning migration.
        if (cause is PostgresException postgres)
        {
            return postgres.SqlState.StartsWith("08", StringComparison.Ordinal) || postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300" or "55000" or "28000" or "28P01" or "3D000"
                ? new DomainError("dependency_unavailable") : null;
        }
        return cause is NpgsqlException or TimeoutException ? new DomainError("dependency_unavailable") : null;
    }
}
