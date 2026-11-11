using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

internal static class PublicationMediaProjection
{
    internal static async Task<IReadOnlyDictionary<Guid, PublicationMediaSnapshot>> LoadAsync(BlogDbContext db, Guid[] ids, CancellationToken ct)
    {
        // One bounded batch for the whole page; no file or author queries inside the result loop.
        var rows = await (from usage in db.PublicationUsages.AsNoTracking()
                          join file in db.MediaFiles.AsNoTracking() on usage.FileId equals file.Id
                          join representation in db.MediaRepresentations.AsNoTracking().Where(r => r.Kind == MediaObjectKind.Display)
                              on file.Id equals representation.FileId into images
                          from image in images.DefaultIfEmpty()
                          where ids.Contains(usage.PublicationId)
                          orderby usage.PublicationId, usage.Role, usage.Position
                          select new
                          {
                              usage.PublicationId,
                              usage.FileId,
                              usage.Role,
                              file.OriginalName,
                              file.OriginalSize,
                              Width = image == null ? 0 : image.Width,
                              Height = image == null ? 0 : image.Height
                          }).ToArrayAsync(ct);
        return ids.ToDictionary(id => id, id =>
        {
            var links = rows.Where(r => r.PublicationId == id).ToArray();
            return new PublicationMediaSnapshot(links.SingleOrDefault(r => r.Role == MediaUsageRole.Cover)?.FileId,
                links.Where(r => r.Role == MediaUsageRole.Inline).Select(r => new PublicationInlineImage(r.FileId, PublicationMediaUrls.Display(id, r.FileId), r.Width, r.Height)).ToArray(),
                links.Where(r => r.Role == MediaUsageRole.Attachment).Select(r => new PublicationAttachment(r.FileId, r.OriginalName, r.OriginalSize, PublicationMediaUrls.Attachment(id, r.FileId))).ToArray());
        });
    }
}
