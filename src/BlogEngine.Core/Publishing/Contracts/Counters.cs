namespace BlogEngine.Core.Publishing.Contracts;

public sealed record Counters(long LikeCount, long ViewCount, bool IsLikedByCurrentUser);
