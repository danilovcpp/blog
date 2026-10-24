using BlogEngine.Core.Shared;

namespace BlogEngine.Core.ReadModels;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasNextPage)
{
    public static PageResult<T> FromLookahead(IReadOnlyList<T> rows, PageRequest request)
    {
        if (rows.Count > request.FetchSize) { throw new ArgumentOutOfRangeException(nameof(rows)); }
        return new(rows.Take(request.PageSize).ToArray(), request.Page, request.PageSize, rows.Count > request.PageSize);
    }
}
