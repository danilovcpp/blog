namespace BlogEngine.Core.Shared;

public sealed record PageRequest
{
    public PageRequest(int page = 1, int pageSize = 20)
    {
        if (page < 1) { throw DomainError.Validation("page"); }
        if (pageSize is < 1 or > 100) { throw DomainError.Validation("pageSize"); }
        Page = page;
        PageSize = pageSize;
        Offset = checked(((long)page - 1) * pageSize);
    }

    public int Page { get; }
    public int PageSize { get; }
    public long Offset { get; }
    public int FetchSize => PageSize + 1;
}
