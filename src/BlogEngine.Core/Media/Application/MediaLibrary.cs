using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Application;

public sealed class MediaLibrary(IMediaLibraryQuery query)
{
    public Task<LibraryPage> ListAsync(CurrentActor actor, Guid? ownerId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) { throw DomainError.Validation("page"); }
        if (pageSize is < 1 or > 100) { throw DomainError.Validation("pageSize"); }
        return query.ListAsync(actor.Id, ownerId, page, pageSize, ct);
    }
    public async Task<LibraryFile> GetAsync(CurrentActor actor, Guid fileId, CancellationToken ct) =>
        await query.GetAsync(actor.Id, fileId, ct) ?? throw new DomainError("resource_not_found");
    public Task<LibraryContent?> ContentAsync(CurrentActor actor, Guid fileId, CancellationToken ct) => query.ContentAsync(actor.Id, fileId, ct);
}
