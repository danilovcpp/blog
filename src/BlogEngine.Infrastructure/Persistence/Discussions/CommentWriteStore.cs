using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Discussions;

public sealed class CommentWriteStore(BlogDbContext db, IPublicationWriteScope scope) : ICommentWriteStore
{
    public Task<CommentSnapshot> CreateAsync(Guid publicationId, Guid? parentId, Func<Comment?, DateTimeOffset, Comment> create, CancellationToken ct) =>
        scope.ExecuteAsync(publicationId, async (session, token) =>
        {
            RequirePublished(session.Publication);
            Comment? parent = null;
            if (parentId is not null)
            {
                parent = await db.Comments.AsNoTracking().SingleOrDefaultAsync(c => c.PublicationId == publicationId && c.Id == parentId &&
                    (c.State == CommentState.Visible || c.VisibleDescendantCount > 0), token) ?? throw new DomainError("resource_not_found");
            }
            var comment = create(parent, session.UtcNow); db.Comments.Add(comment);
            await db.SaveChangesAsync(token);
            await AdjustAncestorsAsync(comment, 1, token);
            return CommentSnapshot.From(comment);
        }, ct);

    public async Task<CommentSnapshot> MutateAsync(Guid commentId, Action<Comment, DateTimeOffset> mutation, CancellationToken ct)
    {
        // The immutable publication link can be read before locking; no state/version is disclosed here.
        var publicationId = await db.Comments.AsNoTracking().Where(c => c.Id == commentId).Select(c => (Guid?)c.PublicationId).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        return await scope.ExecuteAsync(publicationId, async (session, token) =>
        {
            RequirePublished(session.Publication);
            var comment = await db.Comments.SingleAsync(c => c.Id == commentId, token); await db.Entry(comment).ReloadAsync(token);
            var wasVisible = comment.State == CommentState.Visible;
            mutation(comment, session.UtcNow);
            await db.SaveChangesAsync(token);
            var delta = (comment.State == CommentState.Visible ? 1 : 0) - (wasVisible ? 1 : 0);
            if (delta != 0) { await AdjustAncestorsAsync(comment, delta, token); }
            return CommentSnapshot.From(comment);
        }, ct);
    }

    private Task AdjustAncestorsAsync(Comment comment, int delta, CancellationToken ct)
    {
        if (comment.ParentId is not { } parentId) { return Task.CompletedTask; }
        // Only the bounded ancestor chain is visited. Descendants retain their own visibility.
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH RECURSIVE ancestors AS (
                SELECT id,parent_id,1 AS level FROM discussions.comments WHERE publication_id={comment.PublicationId} AND id={parentId}
                UNION ALL
                SELECT c.id,c.parent_id,a.level+1 FROM discussions.comments c JOIN ancestors a ON c.id=a.parent_id
                WHERE c.publication_id={comment.PublicationId} AND a.level<4
            ) UPDATE discussions.comments SET visible_descendant_count=visible_descendant_count+{delta}
            WHERE publication_id={comment.PublicationId} AND id IN (SELECT id FROM ancestors)
            """, ct);
    }
    private static void RequirePublished(PublicationAccess publication)
    { if (!publication.Published || publication.Deleted) { throw new DomainError("resource_not_found"); } }
}
