namespace BlogEngine.Core.Engagement.Domain;

public sealed record Like(Guid PublicationId, Guid UserId, DateTimeOffset CreatedAt);
