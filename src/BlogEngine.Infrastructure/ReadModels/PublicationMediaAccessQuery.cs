using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class PublicationMediaAccessQuery(BlogDbContext db)
{
    public Task<LibraryContent?> FindAsync(Guid? actorId, Guid publicationId, Guid fileId, bool display, CancellationToken ct)
    {
        var links = from publication in db.Publications.AsNoTracking()
                    join usage in db.PublicationUsages.AsNoTracking() on publication.Id equals usage.PublicationId
                    join file in db.MediaFiles.AsNoTracking() on usage.FileId equals file.Id
                    where publication.Id == publicationId && publication.DeletedAt == null && file.Id == fileId && file.State == MediaFileState.Ready &&
                        (publication.State == PublicationState.Published || publication.AuthorId == actorId || db.RoleAssignments.Any(r => r.UserId == actorId && r.Role == LocalRole.Admin)) &&
                        (display ? usage.Role == MediaUsageRole.Cover || usage.Role == MediaUsageRole.Inline : usage.Role == MediaUsageRole.Attachment)
                    select file;
        if (display)
        {
            return (from file in links.Distinct()
                    join image in db.MediaRepresentations.AsNoTracking() on file.Id equals image.FileId
                    join obj in db.MediaObjects.AsNoTracking() on image.ObjectId equals obj.Id
                    where image.Kind == MediaObjectKind.Display && obj.State == MediaObjectState.Present
                    select new LibraryContent(obj.BucketKind, obj.ObjectKey, image.VerifiedLength, image.Sha256, file.OriginalName)).SingleOrDefaultAsync(ct);
        }
        return (from file in links
                join obj in db.MediaObjects.AsNoTracking() on file.OriginalObjectId equals obj.Id
                where obj.State == MediaObjectState.Present && obj.Kind == MediaObjectKind.Source
                select new LibraryContent(obj.BucketKind, obj.ObjectKey, obj.VerifiedLength!.Value, obj.Sha256!, file.OriginalName)).SingleOrDefaultAsync(ct);
    }
}
