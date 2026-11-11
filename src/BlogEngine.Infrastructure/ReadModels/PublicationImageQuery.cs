using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class PublicationImageQuery(BlogDbContext db)
{
    public Task<LibraryContent?> DisplayAsync(Guid actorId, Guid fileId, CancellationToken ct) =>
        (from file in db.MediaFiles.AsNoTracking()
         join image in db.MediaRepresentations.AsNoTracking() on file.Id equals image.FileId
         join obj in db.MediaObjects.AsNoTracking() on image.ObjectId equals obj.Id
         where file.Id == fileId && file.Purpose == MediaPurpose.PublicationImage && file.State == MediaFileState.Ready &&
             (file.OwnerId == actorId || db.RoleAssignments.Any(r => r.UserId == actorId && r.Role == LocalRole.Admin)) &&
             image.Kind == MediaObjectKind.Display && obj.State == MediaObjectState.Present
         select new LibraryContent(obj.BucketKind, obj.ObjectKey, image.VerifiedLength, image.Sha256, file.OriginalName)).SingleOrDefaultAsync(ct);
}
