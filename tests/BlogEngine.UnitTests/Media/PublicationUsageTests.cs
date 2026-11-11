using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Media;

public sealed class PublicationUsageTests
{
    private static MediaFile Ready(Guid owner, MediaPurpose purpose)
    {
        var file = MediaFile.Create(owner, purpose, "image.png", "image/png", 1, DateTimeOffset.UnixEpoch);
        file.MakeReady(purpose == MediaPurpose.Avatar ? null : Guid.NewGuid(), "image/png", DateTimeOffset.UnixEpoch);
        return file;
    }

    [Theory]
    [InlineData(MediaUsageRole.Cover, MediaPurpose.PublicationImage, 1)]
    [InlineData(MediaUsageRole.Inline, MediaPurpose.PublicationImage, 50)]
    [InlineData(MediaUsageRole.Attachment, MediaPurpose.Attachment, 20)]
    public void RolesEnforceOwnerPurposeStateUniquenessAndLimits(MediaUsageRole role, MediaPurpose purpose, int maximum)
    {
        var owner = Guid.NewGuid(); var publication = Guid.NewGuid();
        var files = Enumerable.Range(0, maximum).Select(_ => Ready(owner, purpose)).ToArray();
        var usages = PublicationUsage.ForRole(publication, owner, role, files, false);
        Assert.Equal(Enumerable.Range(0, maximum), usages.Select(x => x.Position));
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, owner, role, [.. files, Ready(owner, purpose)], false));
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, owner, role, [files[0], files[0]], false));
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, Guid.NewGuid(), role, files, false));
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, owner, role, [Ready(owner, MediaPurpose.Avatar)], false));
        var pending = MediaFile.Create(owner, purpose, "x", "image/png", 1, DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, owner, role, [pending], false));
        files[0].Delete(DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(publication, owner, role, files, false));
    }

    [Fact]
    public void CoverAndInlineHaveIndependentPositionsAndExternalCoverIsExclusive()
    {
        var owner = Guid.NewGuid(); var id = Guid.NewGuid(); var image = Ready(owner, MediaPurpose.PublicationImage);
        Assert.Equal(0, Assert.Single(PublicationUsage.ForRole(id, owner, MediaUsageRole.Cover, [image], false)).Position);
        Assert.Equal(0, Assert.Single(PublicationUsage.ForRole(id, owner, MediaUsageRole.Inline, [image], true)).Position);
        Assert.Throws<DomainError>(() => PublicationUsage.ForRole(id, owner, MediaUsageRole.Cover, [image], true));
    }
}
