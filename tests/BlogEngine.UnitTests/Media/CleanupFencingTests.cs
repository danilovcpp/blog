using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Media;

public sealed class CleanupFencingTests
{
    [Fact]
    public void CleanupJobCannotClaimBeforeNotBeforeOrWhileLeased()
    {
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        var job = MediaCleanupJob.Create(Guid.NewGuid(), now.AddMinutes(1));
        Assert.Throws<DomainError>(() => job.Claim(now, TimeSpan.FromMinutes(1)));
        Assert.Throws<DomainError>(() => job.Claim(now.AddMinutes(1), TimeSpan.Zero));
        job.Claim(now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Throws<DomainError>(() => job.Claim(now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        job.Claim(now.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.Equal(2, job.Generation); Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public void CleanupRequiresRetirementAndCurrentUnexpiredGenerationAndKeepsTombstone()
    {
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        var file = MediaFile.Create(Guid.NewGuid(), MediaPurpose.Attachment, "safe.bin", "application/octet-stream", 1, now);
        var upload = MediaUpload.Create(file, Guid.NewGuid(), null, now);
        var obj = MediaObject.Plan(upload, MediaObjectKind.Source, now);
        Assert.Throws<DomainError>(() => obj.ClaimCleanup(now, TimeSpan.FromMinutes(1)));
        obj.Retire(now); var generation = obj.ClaimCleanup(now, TimeSpan.FromMinutes(1));
        Assert.Throws<DomainError>(() => obj.ClaimCleanup(now, TimeSpan.FromMinutes(1)));
        Assert.False(obj.OwnsCleanup(generation, now.AddMinutes(1)));
        var newer = obj.ClaimCleanup(now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Throws<DomainError>(() => obj.FinishCleanup(generation, now.AddMinutes(1), now.AddMinutes(5), true));
        obj.FinishCleanup(newer, now.AddMinutes(1), now.AddMinutes(5), true);
        Assert.Equal(MediaObjectState.Deleted, obj.State); Assert.Equal(now, obj.RetiredAt); Assert.NotNull(obj.LastReconciledAt);
    }
}
