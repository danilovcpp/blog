using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.ReadModels;

public interface ICommentReader
{
    Task<PageResult<CommentItem>> ReadAsync(Guid publicationId, Guid? parentId, PageRequest page, CancellationToken ct);
}
