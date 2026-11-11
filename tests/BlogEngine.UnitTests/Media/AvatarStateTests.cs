using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Media;

public sealed class AvatarStateTests
{
    [Fact]
    public void EmptyDeleteIsNoOpButStaleVersionAlwaysConflicts()
    {
        var state = AvatarState.Empty(Guid.NewGuid(), DateTimeOffset.UnixEpoch); var version = state.Version;
        state.Replace(version, null, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(version, state.Version); Assert.Equal(DateTimeOffset.UnixEpoch, state.UpdatedAt);
        Assert.Equal("concurrency_conflict", Assert.Throws<DomainError>(() => state.Replace(Guid.NewGuid(), null, DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void OnlyReadyAvatarOfSameOwnerCanBeAssigned()
    {
        var owner = Guid.NewGuid(); var state = AvatarState.Empty(owner, DateTimeOffset.UnixEpoch);
        foreach (var file in new[] { MediaFile.Create(owner, MediaPurpose.Avatar, "a", "image/png", 1, DateTimeOffset.UnixEpoch),
            MediaFile.Create(Guid.NewGuid(), MediaPurpose.Avatar, "a", "image/png", 1, DateTimeOffset.UnixEpoch),
            MediaFile.Create(owner, MediaPurpose.PublicationImage, "a", "image/png", 1, DateTimeOffset.UnixEpoch) })
        {
            if (file.OwnerId != owner || file.Purpose != MediaPurpose.Avatar)
            { file.MakeReady(file.Purpose == MediaPurpose.Avatar ? null : Guid.NewGuid(), "image/png", DateTimeOffset.UnixEpoch); }
            Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => state.Replace(state.Version, file, DateTimeOffset.UnixEpoch)).Code);
        }
        var ready = MediaFile.Create(owner, MediaPurpose.Avatar, "a", "image/png", 1, DateTimeOffset.UnixEpoch);
        ready.MakeReady(null, "image/png", DateTimeOffset.UnixEpoch);
        var before = state.Version; state.Replace(before, ready, DateTimeOffset.UnixEpoch);
        Assert.Equal(ready.Id, state.CurrentFileId); Assert.NotEqual(before, state.Version);
    }
}
