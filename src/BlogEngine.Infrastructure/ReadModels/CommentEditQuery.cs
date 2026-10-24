using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class CommentEditQuery(BlogDbContext db)
{
    public async Task<CommentSnapshot> GetAsync(CurrentActor actor, Guid id, CancellationToken ct)
    {
        var accessible = db.Comments.AsNoTracking().Where(c => c.Id == id &&
            db.Publications.Any(p => p.Id == c.PublicationId && p.State == PublicationState.Published && p.DeletedAt == null));
        var owner = await accessible.Select(c => (Guid?)c.AuthorId).SingleOrDefaultAsync(ct) ?? throw new DomainError("resource_not_found");
        var admin = actor.HasRole(LocalRole.Admin);
        if (actor.Id != owner && !admin) { throw new DomainError("forbidden"); }
        return await accessible.Where(c => c.AuthorId == actor.Id || admin)
            .Select(c => new CommentSnapshot(c.Id, c.PublicationId, c.ParentId, c.Depth, c.State.ToString(),
                c.State == CommentState.Deleted || (c.State == CommentState.Hidden && !admin) ? null : c.Text,
                c.AuthorId, c.CreatedAt, c.EditedAt, c.Version)).SingleOrDefaultAsync(ct) ?? throw new DomainError("resource_not_found");
    }
}
