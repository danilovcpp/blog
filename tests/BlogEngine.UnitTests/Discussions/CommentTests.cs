using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Discussions;

[Trait("Category", "Discussions")]
public sealed class CommentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly CurrentActor Owner = new(Guid.NewGuid(), [LocalRole.Reader]);

    [Fact]
    public void CreationTrimsExactScalarsAndKeepsImmutableLinksAndDepth()
    {
        var publication = Guid.NewGuid();
        var root = Comment.Create(publication, Owner.Id, null, "\u0085😀\u3000", Now);
        Assert.Equal("😀", root.Text); Assert.Equal(1, root.Depth); Assert.Equal(CommentState.Visible, root.State);
        Assert.Null(root.ParentId); Assert.Equal(0, root.VisibleDescendantCount); Assert.Null(root.EditedAt);
        var parent = root;
        for (var depth = 2; depth <= 5; depth++)
        {
            var child = Comment.Create(publication, Owner.Id, parent, new string('x', 5000), Now);
            Assert.Equal(depth, child.Depth); Assert.Equal(parent.Id, child.ParentId); parent = child;
        }
        Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => Comment.Create(publication, Owner.Id, parent, "x", Now)).Code);
        Assert.Throws<DomainError>(() => Comment.Create(Guid.NewGuid(), Owner.Id, root, "x", Now));
        foreach (var text in new[] { "", "\u0085\u3000", "a\0", new string('x', 5001), string.Concat(Enumerable.Repeat("😀", 5001)) })
        { Assert.Equal("validation_failed", Assert.Throws<DomainError>(() => Comment.Create(publication, Owner.Id, null, text, Now)).Code); }
        Assert.Equal(5000, TextRules.ScalarLength(Comment.Create(publication, Owner.Id, null, string.Concat(Enumerable.Repeat("😀", 5000)), Now).Text!, "content"));
    }

    [Fact]
    public void OwnershipHiddenAndTerminalDeletionAreEnforcedWithoutChangingLinks()
    {
        var comment = Comment.Create(Guid.NewGuid(), Owner.Id, null, "original", Now);
        var stranger = new CurrentActor(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Author, LocalRole.Moderator]);
        var admin = new CurrentActor(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Admin]);
        Assert.Equal("forbidden", Assert.Throws<DomainError>(() => comment.Edit(stranger, "x", Now)).Code);
        Assert.Equal("forbidden", Assert.Throws<DomainError>(() => comment.Delete(stranger, Now)).Code);
        var id = comment.Id; var pub = comment.PublicationId; var version = comment.Version;
        comment.Edit(Owner, " edited ", Now.AddMinutes(1));
        Assert.Equal("edited", comment.Text); Assert.Equal(Now.AddMinutes(1), comment.EditedAt); Assert.NotEqual(version, comment.Version);
        // Hidden fixtures do not require production moderation commands from the next phase.
        typeof(Comment).GetProperty(nameof(Comment.State))!.SetValue(comment, CommentState.Hidden);
        Assert.Equal("forbidden", Assert.Throws<DomainError>(() => comment.Edit(Owner, "x", Now)).Code);
        comment.Edit(admin, "admin", Now.AddMinutes(2)); Assert.Equal(CommentState.Hidden, comment.State);
        comment.Delete(Owner, Now.AddMinutes(3)); var deletedVersion = comment.Version; var updated = comment.UpdatedAt; var edited = comment.EditedAt;
        Assert.Null(comment.Text); Assert.Equal(CommentState.Deleted, comment.State);
        comment.Delete(admin, Now.AddMinutes(4)); Assert.Equal(deletedVersion, comment.Version); Assert.Equal(updated, comment.UpdatedAt); Assert.Equal(edited, comment.EditedAt);
        Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => comment.Edit(admin, "restore", Now)).Code);
        Assert.Equal(id, comment.Id); Assert.Equal(pub, comment.PublicationId); Assert.Equal(Owner.Id, comment.AuthorId); Assert.Null(comment.ParentId); Assert.Equal(Now, comment.CreatedAt);
    }

    [Fact]
    public async Task ServiceChecksOwnershipBeforeReadingVersionAndRequiresCurrentVersionEvenForRepeatedDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var comment = Comment.Create(Guid.NewGuid(), Owner.Id, null, "original", Now); var service = new CommentService(new SingleCommentStore(comment));
        var stranger = new CurrentActor(Guid.NewGuid(), [LocalRole.Reader, LocalRole.Moderator]);
        Guid MustNotRead() => throw new InvalidOperationException("Unauthorized callers cannot inspect version validation.");
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<DomainError>(() => service.EditAsync(stranger, comment.Id, MustNotRead, "x", ct))).Code);
        var original = comment.Version;
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => service.EditAsync(Owner, comment.Id, Guid.NewGuid, "x", ct))).Code);
        Assert.Equal("original", comment.Text); Assert.Equal(original, comment.Version);
        await service.DeleteAsync(Owner, comment.Id, () => original, ct);
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => service.DeleteAsync(Owner, comment.Id, () => original, ct))).Code);
        var deletedVersion = comment.Version; await service.DeleteAsync(Owner, comment.Id, () => deletedVersion, ct); Assert.Equal(deletedVersion, comment.Version);
        Assert.Equal("invalid_state", Assert.Throws<DomainError>(() => Comment.Create(comment.PublicationId, Owner.Id, comment, "reply", Now)).Code);
    }

    private sealed class SingleCommentStore(Comment comment) : ICommentWriteStore
    {
        public Task<CommentSnapshot> CreateAsync(Guid publicationId, Guid? parentId, Func<Comment?, DateTimeOffset, Comment> create, CancellationToken ct) =>
            Task.FromResult(CommentSnapshot.From(create(null, Now)));
        public Task<CommentSnapshot> MutateAsync(Guid commentId, Action<Comment, DateTimeOffset> mutation, CancellationToken ct)
        { mutation(comment, Now); return Task.FromResult(CommentSnapshot.From(comment)); }
    }
}
