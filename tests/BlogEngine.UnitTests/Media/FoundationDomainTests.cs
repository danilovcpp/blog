using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Media;

public sealed class FoundationDomainTests
{
    [Fact]
    public void NameNormalizationCountsScalarsAndRejectsControlsBeforeTrimming()
    {
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 255)), MediaRules.FileName("\u2003" + string.Concat(Enumerable.Repeat("😀", 255)) + "\u3000"));
        foreach (var invalid in new[] { "x\u0009", "\u0085x", "a/b", "a\\b", ".", " .. ", " C:a", "", "\ud800", new string('a', 256) })
        { Assert.Throws<DomainError>(() => MediaRules.FileName(invalid)); }
    }

    [Fact]
    public void FingerprintIncludesNormalizedPayloadAndExpectedVersionAndExpiryNeverMoves()
    {
        var now = DateTimeOffset.UnixEpoch; var owner = Guid.NewGuid(); var version = Guid.NewGuid();
        var first = MediaFile.Create(owner, MediaPurpose.Avatar, " x.png ", "IMAGE/PNG", 20, now);
        var second = MediaFile.Create(owner, MediaPurpose.Avatar, "x.png", "image/png", 20, now);
        var upload = MediaUpload.Create(first, Guid.NewGuid(), version, now);
        Assert.Equal(upload.RequestFingerprint, MediaUpload.Create(second, Guid.NewGuid(), version, now).RequestFingerprint);
        Assert.NotEqual(upload.RequestFingerprint, MediaUpload.Create(second, Guid.NewGuid(), Guid.NewGuid(), now).RequestFingerprint);
        var generation = upload.Claim(now, TimeSpan.FromSeconds(10));
        Assert.Throws<DomainError>(() => upload.AcceptContent(generation, Guid.NewGuid(), 20, new byte[32], now.AddSeconds(10)));
        Assert.Equal(now.AddHours(24), upload.ExpiresAt); Assert.Equal(MediaUploadState.Expired, upload.EffectiveState(now.AddHours(24)));
    }

    [Fact]
    public void ReadyContentCannotChangeAndDeletedFileKeepsOwnershipTombstone()
    {
        var now = DateTimeOffset.UnixEpoch; var file = MediaFile.Create(Guid.NewGuid(), MediaPurpose.Attachment, "x", "text/plain", 10, now);
        var owner = file.OwnerId; var id = file.Id;
        file.MakeReady(Guid.NewGuid(), "text/plain", now);
        Assert.Throws<DomainError>(() => file.MakeReady(Guid.NewGuid(), "text/plain", now));
        file.Delete(now); file.Delete(now.AddDays(1));
        Assert.Equal(owner, file.OwnerId); Assert.Equal(id, file.Id); Assert.Null(file.OriginalObjectId); Assert.Equal(now, file.DeletedAt);
    }
}
