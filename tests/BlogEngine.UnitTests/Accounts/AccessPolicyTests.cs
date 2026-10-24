using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Accounts;

public sealed class AccessPolicyTests
{
    [Fact]
    public void ReaderIsImplicitAuthorAndModeratorAreIndependentAndAdminCombinesRights()
    {
        var reader = new CurrentActor(Guid.NewGuid(), []);
        var author = new CurrentActor(Guid.NewGuid(), [LocalRole.Author]);
        var moderator = new CurrentActor(Guid.NewGuid(), [LocalRole.Moderator]);
        var admin = new CurrentActor(Guid.NewGuid(), [LocalRole.Admin]);
        Assert.Equal([LocalRole.Reader], reader.Roles);
        Assert.False(AccessPolicy.Allows(reader, ActorAction.CreatePublication));
        Assert.True(AccessPolicy.Allows(author, ActorAction.CreatePublication));
        Assert.False(AccessPolicy.Allows(author, ActorAction.ModerateComments));
        Assert.True(AccessPolicy.Allows(moderator, ActorAction.ModerateComments));
        Assert.False(AccessPolicy.Allows(moderator, ActorAction.CreatePublication));
        Assert.All(Enum.GetValues<ActorAction>(), action => Assert.True(AccessPolicy.Allows(admin, action)));
        Assert.False(AccessPolicy.Allows(admin, (ActorAction)999));
        Assert.False(AccessPolicy.Allows(null, ActorAction.Interact));
    }

    [Fact]
    public void HiddenResourceIsConcealedBeforeVersionComparison()
    {
        var actor = new CurrentActor(Guid.NewGuid(), [LocalRole.Author]);
        Assert.Equal("resource_not_found", Assert.Throws<DomainError>(() =>
            AccessPolicy.DemandPublicationEdit(actor, Guid.NewGuid(), false, false)).Code);
        Assert.Equal("forbidden", Assert.Throws<DomainError>(() =>
            AccessPolicy.DemandPublicationEdit(actor, Guid.NewGuid(), true, false)).Code);
        AccessPolicy.DemandPublicationEdit(actor, actor.Id, false, false);
        Assert.Equal("resource_not_found", Assert.Throws<DomainError>(() =>
            AccessPolicy.DemandPublicationEdit(actor, actor.Id, true, true)).Code);
    }
}
