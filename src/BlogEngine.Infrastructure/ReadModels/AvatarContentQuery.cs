using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class AvatarContentQuery(BlogDbContext db)
{
    public Task<LibraryContent?> FindAsync(Guid fileId, int size, CancellationToken ct) =>
        (from state in db.AvatarStates.AsNoTracking()
         join file in db.MediaFiles.AsNoTracking() on state.CurrentFileId equals file.Id
         join image in db.MediaRepresentations.AsNoTracking() on file.Id equals image.FileId
         join obj in db.MediaObjects.AsNoTracking() on image.ObjectId equals obj.Id
         where file.Id == fileId && file.State == MediaFileState.Ready && file.Purpose == MediaPurpose.Avatar &&
             image.Kind == (size == 256 ? MediaObjectKind.Avatar256 : MediaObjectKind.Avatar512) && obj.State == MediaObjectState.Present
         select new LibraryContent(obj.BucketKind, obj.ObjectKey, image.VerifiedLength, image.Sha256, "")).SingleOrDefaultAsync(ct);
}
