using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class PublicFeedQuery(BlogDbContext db) : IPublicFeedReader
{
    public async Task<PageResult<PublicationFeed>> ReadAsync(PublicationFeedRequest request, Guid? actorId, CancellationToken cancellationToken)
    {
        var categories = request.CategoryIds.ToArray(); var tags = request.TagIds.ToArray();
        if (categories.Length != 0)
        {
            var existing = await db.Categories.AsNoTracking().Where(c => categories.Contains(c.Id)).Select(c => c.Id).ToArrayAsync(cancellationToken);
            if (existing.Length != categories.Length) { throw DomainError.Validation("categoryIds"); }
        }
        if (tags.Length != 0)
        {
            var existing = await db.Tags.AsNoTracking().Where(t => tags.Contains(t.Id)).Select(t => t.Id).ToArrayAsync(cancellationToken);
            if (existing.Length != tags.Length) { throw DomainError.Validation("tagIds"); }
        }
        // Only these fixed SQL fragments choose the order; every request value is a typed parameter.
        var order = request.Sort == PublicationSort.Popular ? "p.like_count DESC, p.first_published_at DESC, p.id DESC" : "p.first_published_at DESC, p.id DESC";
        var sql = """
            SELECT p.id AS "Id", p.title AS "Title", p.slug AS "Slug", p.description AS "Description", p.cover_url AS "CoverUrl",
                p.author_id AS "AuthorId", p.created_at AS "CreatedAt", p.updated_at AS "UpdatedAt", p.first_published_at AS "FirstPublishedAt",
                p.like_count AS "LikeCount", p.view_count AS "ViewCount",
                (@actor_id IS NOT NULL AND EXISTS (SELECT 1 FROM engagement.likes l WHERE l.publication_id=p.id AND l.user_id=@actor_id)) AS "IsLikedByCurrentUser"
            FROM publishing.publications p
            WHERE p.state = 1 AND p.deleted_at IS NULL
                AND (cardinality(@category_ids)=0 OR EXISTS (SELECT 1 FROM publishing.publication_categories c WHERE c.publication_id=p.id AND c.category_id=ANY(@category_ids)))
                AND NOT EXISTS (SELECT 1 FROM unnest(@tag_ids) f(id) WHERE NOT EXISTS (SELECT 1 FROM publishing.publication_tags t WHERE t.publication_id=p.id AND t.tag_id=f.id))
            ORDER BY
            """ + " " + order + " LIMIT @fetch_size OFFSET @offset";
        var rows = await db.Database.SqlQueryRaw<FeedRow>(sql,
            new NpgsqlParameter("actor_id", NpgsqlDbType.Uuid) { Value = (object?)actorId ?? DBNull.Value },
            new NpgsqlParameter("category_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = categories },
            new NpgsqlParameter("tag_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = tags },
            new NpgsqlParameter("fetch_size", NpgsqlDbType.Integer) { Value = request.Page.FetchSize },
            new NpgsqlParameter("offset", NpgsqlDbType.Bigint) { Value = request.Page.Offset }).ToListAsync(cancellationToken);
        var page = PageResult<FeedRow>.FromLookahead(rows, request.Page);
        if (page.Items.Count == 0) { return new([], page.Page, page.PageSize, page.HasNextPage); }
        var classification = await PublicationClassificationQuery.LoadAsync(db, page.Items.Select(p => p.Id).ToArray(), cancellationToken);
        return new(page.Items.Select(p => new PublicationFeed(p.Id, p.Title, p.Slug, p.Description, p.CoverUrl, new(p.AuthorId), p.CreatedAt, p.UpdatedAt,
            p.FirstPublishedAt, classification.Categories[p.Id].ToArray(), classification.Tags[p.Id].ToArray(), p.LikeCount, p.ViewCount, p.IsLikedByCurrentUser)).ToArray(),
            page.Page, page.PageSize, page.HasNextPage);
    }

    private sealed record FeedRow(Guid Id, string Title, string Slug, string? Description, string? CoverUrl, Guid AuthorId,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset FirstPublishedAt, long LikeCount, long ViewCount, bool IsLikedByCurrentUser);
}
