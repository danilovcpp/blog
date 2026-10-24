namespace BlogEngine.Core.ReadModels;

public sealed record Counters(long LikeCount, long ViewCount, bool IsLikedByCurrentUser);
