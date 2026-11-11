using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Media;

public sealed class UploadRulesTests
{
    [Theory]
    [InlineData(MediaPurpose.Attachment, 52428800)]
    [InlineData(MediaPurpose.PublicationImage, 10485760)]
    [InlineData(MediaPurpose.Avatar, 5242880)]
    public void SizeAndTypeBoundaries(MediaPurpose purpose, long maximum)
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(maximum, MediaFile.Create(Guid.NewGuid(), purpose, "x", "IMAGE/PNG", maximum, now).OriginalSize);
        foreach (var size in new[] { 0, -1, maximum + 1 })
        { Assert.Throws<DomainError>(() => MediaFile.Create(Guid.NewGuid(), purpose, "x", "image/png", size, now)); }
        foreach (var type in new[] { "image/png;charset=utf8", "image/", "image/ png", "image/svg+xml" })
        {
            if (purpose == MediaPurpose.Attachment && type == "image/svg+xml") { continue; }
            Assert.Throws<DomainError>(() => MediaRules.ContentType(type, purpose));
        }
        Assert.Throws<DomainError>(() => MediaRules.MaximumSize((MediaPurpose)99));
    }

    [Fact]
    public void NamesCountScalarsAndInspectControlsBeforeTrim()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 255));
        Assert.Equal(name, MediaRules.FileName("\u2003\u00a0" + name + "\u3000"));
        foreach (var invalid in new[] { name + "a", "\ud800", "\udc00", "", ".", " .. ", "a/b", "a\\b", "C:x", " z: ", "x\t", "\nx", "\u0085x" })
        { Assert.Throws<DomainError>(() => MediaRules.FileName(invalid)); }
        Assert.Equal("файл:данные", MediaRules.FileName("файл:данные"));
    }

    [Fact]
    public void FingerprintsAndFencesProtectTheHistoricalResult()
    {
        var now = DateTimeOffset.UnixEpoch; var owner = Guid.NewGuid();
        var file = MediaFile.Create(owner, MediaPurpose.Attachment, " same ", "TEXT/PLAIN", 1, now);
        var upload = MediaUpload.Create(file, Guid.NewGuid(), null, now);
        var same = MediaFile.Create(owner, MediaPurpose.Attachment, "same", "text/plain", 1, now);
        Assert.Equal(upload.RequestFingerprint, MediaUpload.Create(same, Guid.NewGuid(), null, now).RequestFingerprint);
        Assert.Throws<DomainError>(() => upload.StartProcessing(now));
        var generation = upload.Claim(now, TimeSpan.FromSeconds(60));
        Assert.Throws<DomainError>(() => upload.Claim(now, TimeSpan.FromSeconds(60)));
        Assert.Throws<DomainError>(() => upload.AcceptContent(generation + 1, Guid.NewGuid(), 1, new byte[32], now));
        upload.AcceptContent(generation, Guid.NewGuid(), 1, new byte[32], now);
        upload.StartProcessing(now);
        generation = upload.Claim(now, TimeSpan.FromSeconds(60));
        upload.Complete(generation, "{\"historical\":true}", now);
        Assert.Equal(MediaUploadState.Completed, upload.EffectiveState(now.AddDays(2)));
        Assert.Throws<DomainError>(() => upload.Terminate(MediaUploadState.Cancelled, "cancelled", now));
        Assert.Equal("{\"historical\":true}", upload.Result);
    }

    [Fact]
    public void AnActiveReplayCannotQueueProcessingAndRenewalRequiresTheCurrentFence()
    {
        var now = DateTimeOffset.UnixEpoch;
        var file = MediaFile.Create(Guid.NewGuid(), MediaPurpose.Attachment, "x", "text/plain", 1, now);
        var upload = MediaUpload.Create(file, Guid.NewGuid(), null, now);
        var generation = upload.Claim(now, TimeSpan.FromSeconds(60));
        upload.AcceptContent(generation, Guid.NewGuid(), 1, new byte[32], now);
        generation = upload.Claim(now, TimeSpan.FromSeconds(60));
        Assert.Equal("upload_in_progress", Assert.Throws<DomainError>(() => upload.StartProcessing(now)).Code);
        upload.ReleaseTransfer(generation - 1);
        Assert.Throws<DomainError>(() => upload.StartProcessing(now));
        upload.ReleaseTransfer(generation); upload.StartProcessing(now);
        generation = upload.Claim(now, TimeSpan.FromSeconds(60));
        upload.RenewLease(generation, now.AddSeconds(30), TimeSpan.FromSeconds(60));
        Assert.Equal(now.AddSeconds(90), upload.LeaseUntil);
        Assert.Throws<DomainError>(() => upload.RenewLease(generation - 1, now.AddSeconds(31), TimeSpan.FromSeconds(60)));
        Assert.Equal(now.AddHours(24), upload.ExpiresAt);
    }
}
