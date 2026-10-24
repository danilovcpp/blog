using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Accounts.Application;

public enum ActorAction { Interact, CreatePublication, ManageTaxonomy, ModerateComments }

public static class AccessPolicy
{
    public static bool Allows(CurrentActor? actor, ActorAction action) => actor is not null && action switch
    {
        ActorAction.Interact => true,
        ActorAction.CreatePublication => actor.HasRole(LocalRole.Author) || actor.HasRole(LocalRole.Admin),
        ActorAction.ManageTaxonomy => actor.HasRole(LocalRole.Admin),
        ActorAction.ModerateComments => actor.HasRole(LocalRole.Moderator) || actor.HasRole(LocalRole.Admin),
        _ => false,
    };

    public static void Demand(CurrentActor? actor, ActorAction action)
    {
        if (actor is null) { throw new DomainError("authentication_required"); }
        if (!Allows(actor, action)) { throw new DomainError("forbidden"); }
    }

    public static void DemandPublicationEdit(CurrentActor actor, Guid ownerId, bool published, bool deleted)
    {
        if (deleted || (!published && actor.Id != ownerId && !actor.HasRole(LocalRole.Admin)))
        {
            throw new DomainError("resource_not_found");
        }
        Demand(actor, ActorAction.CreatePublication);
        if (actor.Id != ownerId && !actor.HasRole(LocalRole.Admin)) { throw new DomainError("forbidden"); }
    }
}
