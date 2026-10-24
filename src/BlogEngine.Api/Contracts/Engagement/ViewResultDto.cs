namespace BlogEngine.Api.Contracts.Engagement;

public sealed record ViewResultDto(bool Counted, long ViewCount, string? AnonymousVisitorId);
