namespace BlogEngine.Core.Engagement.Contracts;

public sealed record ViewRegistration(bool Counted, long ViewCount);

public interface IViewWriteStore
{
    Task<ViewRegistration> RegisterAsync(Guid publicationId, VisitorIdentity visitor, CancellationToken ct);
}
