using BlogEngine.Core.Publishing.Contracts;

namespace BlogEngine.Api.Contracts.Reading;

public sealed record CountersDto(long LikeCount, long ViewCount, bool IsLikedByCurrentUser)
{
    public static CountersDto From(Counters counters) => new(counters.LikeCount, counters.ViewCount, counters.IsLikedByCurrentUser);
}
