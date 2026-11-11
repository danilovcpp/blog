using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class MediaLibraryQuery(BlogDbContext db) : IMediaLibraryQuery
{
    private IQueryable<MediaFile> Visible(Guid actorId) => db.MediaFiles.AsNoTracking().Where(f =>
        f.State == MediaFileState.Ready && f.Purpose != MediaPurpose.Avatar &&
        (f.OwnerId == actorId || db.RoleAssignments.Any(r => r.UserId == actorId && r.Role == LocalRole.Admin)));

    public async Task<LibraryPage> ListAsync(Guid actorId, Guid? ownerId, int page, int pageSize, CancellationToken ct)
    {
        // Explicit owner selection and the page share a database snapshot, including current Admin grants.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        if (ownerId.HasValue && !await db.RoleAssignments.AnyAsync(r => r.UserId == actorId && r.Role == LocalRole.Admin, ct))
        { throw new DomainError("forbidden"); }
        var owner = ownerId ?? actorId;
        var offset = ((long)page - 1) * pageSize;
        var files = await db.MediaFiles.FromSqlInterpolated($"SELECT * FROM media.files WHERE owner_id={owner} AND state=1 AND purpose IN (0,1) ORDER BY created_at DESC,id DESC LIMIT {pageSize + 1} OFFSET {offset}")
            .AsNoTracking().OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Select(f => new LibraryFile(f.Id, f.OwnerId, f.Purpose, f.OriginalName, f.OriginalSize, f.VerifiedContentType!, f.CreatedAt)).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return new(page, pageSize, files.Take(pageSize).ToArray(), files.Length > pageSize);
    }

    public Task<LibraryFile?> GetAsync(Guid actorId, Guid fileId, CancellationToken ct) => Visible(actorId).Where(f => f.Id == fileId)
        .Select(f => new LibraryFile(f.Id, f.OwnerId, f.Purpose, f.OriginalName, f.OriginalSize, f.VerifiedContentType!, f.CreatedAt)).SingleOrDefaultAsync(ct);

    public Task<LibraryContent?> ContentAsync(Guid actorId, Guid fileId, CancellationToken ct) =>
        (from f in Visible(actorId)
         join o in db.MediaObjects.AsNoTracking() on f.OriginalObjectId equals o.Id
         where f.Id == fileId && o.State == MediaObjectState.Present && o.Kind == MediaObjectKind.Source
         select new LibraryContent(o.BucketKind, o.ObjectKey, o.VerifiedLength!.Value, o.Sha256!, f.OriginalName)).SingleOrDefaultAsync(ct);
}
