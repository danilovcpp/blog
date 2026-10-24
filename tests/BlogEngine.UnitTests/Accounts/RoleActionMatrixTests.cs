using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Accounts;

public sealed class RoleActionMatrixTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static IEnumerable<object[]> Matrix()
    {
        LocalRole[][] assignments = [[], [LocalRole.Reader], [LocalRole.Author], [LocalRole.Moderator], [LocalRole.Author, LocalRole.Moderator], [LocalRole.Admin]];
        foreach (var roles in assignments)
        {
            foreach (var action in Enum.GetValues<ActorAction>())
            {
                yield return [roles, action];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ActionsRespectLocalRolesAndAnonymousIsDenied(LocalRole[] roles, ActorAction action)
    {
        // The empty assignment set models anonymous here; Reader is explicit for authenticated rows.
        CurrentActor? actor = roles.Length == 0 ? null : new CurrentActor(Owner, roles);
        var expected = actor is not null && (roles.Contains(LocalRole.Admin) || action switch
        {
            ActorAction.Interact => true,
            ActorAction.CreatePublication => roles.Contains(LocalRole.Author),
            ActorAction.ManageTaxonomy => false,
            ActorAction.ModerateComments => roles.Contains(LocalRole.Moderator),
            _ => false,
        });
        Assert.Equal(expected, AccessPolicy.Allows(actor, action));
        if (expected) { AccessPolicy.Demand(actor, action); }
        else { Assert.Equal(actor is null ? "authentication_required" : "forbidden", Assert.Throws<DomainError>(() => AccessPolicy.Demand(actor, action)).Code); }
        if (actor is null) { return; }
        foreach (var own in new[] { false, true })
        {
            foreach (var published in new[] { false, true })
            {
                foreach (var deleted in new[] { false, true })
                {
                    var admin = roles.Contains(LocalRole.Admin);
                    var code = deleted || (!published && !own && !admin) ? "resource_not_found"
                        : !admin && (!roles.Contains(LocalRole.Author) || !own) ? "forbidden" : null;
                    var ownerId = own ? Owner : Guid.Empty;
                    if (code is null) { AccessPolicy.DemandPublicationEdit(actor, ownerId, published, deleted); }
                    else { Assert.Equal(code, Assert.Throws<DomainError>(() => AccessPolicy.DemandPublicationEdit(actor, ownerId, published, deleted)).Code); }
                }
            }
        }
    }
}
