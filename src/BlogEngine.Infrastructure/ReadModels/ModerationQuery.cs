using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class ModerationQuery(BlogDbContext db, IPublicationReader publications)
{
    public async Task<PageResult<CommentSnapshot>> ListAsync(CurrentActor actor, Guid? publicationId, CommentState? state, PageRequest page, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.ModerateComments);
        if (publicationId is { } id)
        {
            var parent = await publications.FindAccessAsync(id, ct);
            if (parent is null || !parent.Published || parent.Deleted) { throw new DomainError("resource_not_found"); }
        }
        var stateCode = state is null ? (short?)null : (short)state;
        var rows = await db.Comments.FromSqlInterpolated($"""
            SELECT c.* FROM discussions.comments c
            JOIN publishing.publications p ON p.id=c.publication_id AND p.state=1 AND p.deleted_at IS NULL
            WHERE ({publicationId}::uuid IS NULL OR c.publication_id={publicationId})
            AND ({stateCode}::smallint IS NULL OR c.state={stateCode})
            ORDER BY c.created_at DESC,c.id DESC LIMIT {page.FetchSize} OFFSET {page.Offset}
            """).AsNoTracking().OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Select(c => new CommentSnapshot(c.Id, c.PublicationId, c.ParentId, c.Depth, c.State.ToString(),
                c.State == CommentState.Deleted ? null : c.Text, c.AuthorId, c.CreatedAt, c.EditedAt, c.Version)).ToListAsync(ct);
        return PageResult<CommentSnapshot>.FromLookahead(rows, page);
    }
}
