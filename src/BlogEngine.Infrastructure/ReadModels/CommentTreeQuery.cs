using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class CommentTreeQuery(BlogDbContext db, IPublicationReader publications) : ICommentReader
{
    public async Task<PageResult<CommentItem>> ReadAsync(Guid publicationId, Guid? parentId, PageRequest page, CancellationToken ct)
    {
        var publication = await publications.FindAccessAsync(publicationId, ct);
        if (publication is null || !publication.Published || publication.Deleted) { throw new DomainError("resource_not_found"); }
        if (parentId is not null && !await db.Comments.AsNoTracking().AnyAsync(c => c.Id == parentId && c.PublicationId == publicationId &&
            (c.State == CommentState.Visible || c.VisibleDescendantCount > 0), ct)) { throw new DomainError("resource_not_found"); }
        // Separate predicates let PostgreSQL seek the composite parent/order index for both roots and replies.
        FormattableString sql = parentId is { } parent ? (FormattableString)$"""
            SELECT c.* FROM discussions.comments c
            JOIN publishing.publications p ON p.id=c.publication_id AND p.state=1 AND p.deleted_at IS NULL
            WHERE c.publication_id={publicationId} AND c.parent_id={parent}
            AND (c.state=0 OR c.visible_descendant_count>0)
            ORDER BY c.created_at,c.id LIMIT {page.FetchSize} OFFSET {page.Offset}
            """ : (FormattableString)$"""
            SELECT c.* FROM discussions.comments c
            JOIN publishing.publications p ON p.id=c.publication_id AND p.state=1 AND p.deleted_at IS NULL
            WHERE c.publication_id={publicationId} AND c.parent_id IS NULL
            AND (c.state=0 OR c.visible_descendant_count>0)
            ORDER BY c.created_at,c.id LIMIT {page.FetchSize} OFFSET {page.Offset}
            """;
        var rows = await db.Comments.FromSqlInterpolated(sql).AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CommentItem(c.Id, c.PublicationId, c.ParentId, c.Depth, c.State.ToString(),
                c.State == CommentState.Visible ? c.Text : null, c.State == CommentState.Visible ? c.AuthorId : null,
                c.CreatedAt, c.EditedAt, db.Comments.Any(child => child.PublicationId == publicationId && child.ParentId == c.Id &&
                    (child.State == CommentState.Visible || child.VisibleDescendantCount > 0)))).ToListAsync(ct);
        return PageResult<CommentItem>.FromLookahead(rows, page);
    }
}
