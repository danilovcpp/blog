using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Application;

public sealed class UploadLifecycle(IUploadLifecycleStore store, IEnumerable<IMediaUploadProcessor> processors)
{
    public Task<UploadSnapshot> InitializeAsync(CurrentActor actor, MediaPurpose purpose, string name, long size, string contentType,
        Guid key, Guid? expectedAvatarVersion, CancellationToken ct)
    {
        if (!processors.Any(p => p.Purpose == purpose)) { throw DomainError.Validation("purpose"); }
        // The store supplies the database timestamp inside the reservation transaction.
        var file = MediaFile.Create(actor.Id, purpose, name, contentType, size, DateTimeOffset.UnixEpoch);
        var upload = MediaUpload.Create(file, key, expectedAvatarVersion, DateTimeOffset.UnixEpoch);
        return store.InitializeAsync(file, upload, ct);
    }

    public Task<UploadSnapshot> GetAsync(CurrentActor actor, Guid id, CancellationToken ct) => store.GetAsync(actor.Id, id, ct);
    public Task CancelAsync(CurrentActor actor, Guid id, CancellationToken ct) => store.CancelAsync(actor.Id, id, ct);
}
