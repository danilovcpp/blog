using BlogEngine.Core.Engagement.Contracts;

namespace BlogEngine.Core.Engagement.Application;

public sealed class ViewService(IViewWriteStore store)
{
    public Task<ViewRegistration> RegisterAsync(Guid publicationId, VisitorIdentity visitor, CancellationToken ct) =>
        store.RegisterAsync(publicationId, visitor, ct);
}
