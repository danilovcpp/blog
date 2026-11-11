using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class PublicationMediaUsages(BlogDbContext db) : IPublicationMediaUsages
{
    public async Task<string?> ReplaceAsync(Guid publicationId, Guid ownerId, string? requestedCoverUrl, PublicationMediaChange change, CancellationToken ct)
    {
        await LockOwnerAsync(ownerId, ct);
        var current = await db.PublicationUsages.AsNoTracking().Where(u => u.PublicationId == publicationId).ToArrayAsync(ct);
        var cover = change.CoverSpecified ? change.CoverFileId : current.SingleOrDefault(u => u.Role == MediaUsageRole.Cover)?.FileId;
        var external = requestedCoverUrl;
        if (requestedCoverUrl is not null && PublicationMediaUrls.IsManaged(requestedCoverUrl))
        {
            var oldCover = current.SingleOrDefault(u => u.Role == MediaUsageRole.Cover)?.FileId;
            if (change.CoverSpecified || oldCover is null || requestedCoverUrl != PublicationMediaUrls.Display(publicationId, oldCover.Value))
            { throw DomainError.Validation("coverUrl"); }
            external = null;
        }
        else if (requestedCoverUrl is not null)
        {
            if (change.CoverSpecified && cover is not null) { throw DomainError.Validation("coverUrl"); }
            cover = null;
        }
        var inline = change.InlineImageIds ?? current.Where(u => u.Role == MediaUsageRole.Inline).OrderBy(u => u.Position).Select(u => u.FileId).ToArray();
        var attachments = change.AttachmentIds ?? current.Where(u => u.Role == MediaUsageRole.Attachment).OrderBy(u => u.Position).Select(u => u.FileId).ToArray();
        var roles = new[] { (MediaUsageRole.Cover, (IReadOnlyList<Guid>)(cover is { } c ? new[] { c } : [])), (MediaUsageRole.Inline, inline), (MediaUsageRole.Attachment, attachments) };
        foreach (var (role, filesInRole) in roles) { PublicationUsage.ValidateIds(role, filesInRole); }
        var ids = roles.SelectMany(r => r.Item2).Distinct().Order().ToArray();
        var files = new Dictionary<Guid, MediaFile>();
        foreach (var id in ids)
        {
            var file = await db.MediaFiles.FromSqlInterpolated($"SELECT * FROM media.files WHERE id={id} AND owner_id={ownerId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (file is null) { throw DomainError.Validation("files"); }
            files.Add(id, file);
        }
        var replacements = roles.SelectMany(r => PublicationUsage.ForRole(publicationId, ownerId, r.Item1, r.Item2.Select(id => files[id]).ToArray(), external is not null)).ToArray();
        await db.PublicationUsages.Where(u => u.PublicationId == publicationId).ExecuteDeleteAsync(ct);
        db.PublicationUsages.AddRange(replacements);
        await db.SaveChangesAsync(ct);
        return external;
    }

    public async Task RemoveAsync(Guid publicationId, Guid ownerId, CancellationToken ct)
    {
        await LockOwnerAsync(ownerId, ct);
        await db.PublicationUsages.Where(u => u.PublicationId == publicationId).ExecuteDeleteAsync(ct);
    }

    private async Task LockOwnerAsync(Guid ownerId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) { throw new InvalidOperationException("A Publishing transaction is required."); }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.allowances(owner_id,limit_bytes,used_bytes) VALUES ({ownerId},{MediaRules.DefaultAllowance},0) ON CONFLICT DO NOTHING", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM media.allowances WHERE owner_id={ownerId} FOR UPDATE", ct);
    }
}
