using BlogEngine.Core.Media.Contracts;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class AuthorAvatarQuery(BlogDbContext db) : IAuthorAvatarReader
{
    // Media owns this fragment; callers compose it into their final SQL projection.
    internal static IQueryable<AvatarAssignment> Assignments(BlogDbContext context) => context.AvatarStates.AsNoTracking()
        .Where(a => a.CurrentFileId != null).Select(a => new AvatarAssignment { AuthorId = a.UserId, FileId = a.CurrentFileId });

    public async Task<IReadOnlyDictionary<Guid, AvatarReference?>> ReadAsync(IReadOnlyCollection<Guid> authorIds, CancellationToken ct)
    {
        if (authorIds.Count > 100) { throw new ArgumentOutOfRangeException(nameof(authorIds)); }
        var ids = authorIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => (AvatarReference?)null);
        if (ids.Length == 0) { return result; }
        var rows = await Assignments(db).Where(a => ids.Contains(a.AuthorId)).ToArrayAsync(ct);
        foreach (var row in rows) { result[row.AuthorId] = AvatarReference.From(row.FileId); }
        return result;
    }
    internal sealed class AvatarAssignment
    {
        public Guid AuthorId { get; init; }
        public Guid? FileId { get; init; }
    }
}
