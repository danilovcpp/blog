using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Discussions;

[Trait("Category", "Moderation")]
public sealed class ModerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly CurrentActor Owner = new(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Author]);
    private static readonly CurrentActor Moderator = new(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Moderator]);
    private static readonly CurrentActor Admin = new(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Admin]);

    [Fact]
    public void TransitionsRetainTextAndAuthorAndOnlyChangeStateVersionAndUpdatedAt()
    {
        var comment = Comment.Create(Guid.NewGuid(), Owner.Id, null, "retained", Now);
        var original = comment.Version;
        comment.Hide(Moderator, Now.AddMinutes(1));
        Assert.Equal(CommentState.Hidden, comment.State); Assert.NotEqual(original, comment.Version);
        Assert.Equal(Now.AddMinutes(1), comment.UpdatedAt); Assert.Null(comment.EditedAt);
        var hiddenVersion = comment.Version;
        comment.Hide(Admin, Now.AddMinutes(2));
        Assert.Equal(hiddenVersion, comment.Version); Assert.Equal(Now.AddMinutes(1), comment.UpdatedAt);
        comment.Edit(Admin, "admin edit", Now.AddMinutes(3));
        Assert.Equal(CommentState.Hidden, comment.State); var edited = comment.EditedAt;
        comment.Restore(Moderator, Now.AddMinutes(4));
        Assert.Equal(CommentState.Visible, comment.State); Assert.Equal("admin edit", comment.Text);
        Assert.Equal(Owner.Id, comment.AuthorId); Assert.Equal(Now, comment.CreatedAt); Assert.Equal(edited, comment.EditedAt);
        var visibleVersion = comment.Version;
        comment.Restore(Admin, Now.AddMinutes(5));
        Assert.Equal(visibleVersion, comment.Version); Assert.Equal(Now.AddMinutes(4), comment.UpdatedAt);
        Assert.Equal(0, comment.VisibleDescendantCount);
        comment.Delete(Owner, Now.AddMinutes(6));
        Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => comment.Hide(Moderator, Now)).Code);
        Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => comment.Restore(Admin, Now)).Code);
        Assert.Null(comment.Text);
    }

    [Fact]
    public async Task VersionIsCheckedAfterPermissionAndBeforeNoOpOrTerminalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var comment = Comment.Create(Guid.NewGuid(), Owner.Id, null, "retained", Now);
        var service = new ModerationService(new Store(comment));
        Guid MustNotRead() => throw new InvalidOperationException("Permission must precede version.");
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<DomainError>(() => service.HideAsync(Owner, comment.Id, MustNotRead, ct))).Code);
        var original = comment.Version;
        await service.HideAsync(Moderator, comment.Id, () => original, ct);
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => service.HideAsync(Moderator, comment.Id, () => original, ct))).Code);
        var hiddenVersion = comment.Version; var updated = comment.UpdatedAt;
        await service.HideAsync(Admin, comment.Id, () => hiddenVersion, ct);
        Assert.Equal(hiddenVersion, comment.Version); Assert.Equal(updated, comment.UpdatedAt);
        await service.RestoreAsync(Admin, comment.Id, () => hiddenVersion, ct);
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => service.RestoreAsync(Admin, comment.Id, () => hiddenVersion, ct))).Code);
        comment.Delete(Owner, Now);
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => service.RestoreAsync(Admin, comment.Id, Guid.NewGuid, ct))).Code);
        Assert.Equal("invalid_state", (await Assert.ThrowsAsync<DomainError>(() => service.RestoreAsync(Admin, comment.Id, () => comment.Version, ct))).Code);
    }

    private sealed class Store(Comment comment) : ICommentWriteStore
    {
        public Task<CommentSnapshot> CreateAsync(Guid publicationId, Guid? parentId, Func<Comment?, DateTimeOffset, Comment> create, CancellationToken ct) =>
            Task.FromResult(CommentSnapshot.From(create(null, Now)));
        public Task<CommentSnapshot> MutateAsync(Guid commentId, Action<Comment, DateTimeOffset> mutation, CancellationToken ct)
        { mutation(comment, Now); return Task.FromResult(CommentSnapshot.From(comment)); }
    }
}
