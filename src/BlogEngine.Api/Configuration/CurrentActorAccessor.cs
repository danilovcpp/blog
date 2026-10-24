using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Configuration;

public sealed class CurrentActorAccessor(IHttpContextAccessor http, IActorResolver resolver)
{
    private CurrentActor? actor;

    public async Task<CurrentActor> GetRequiredAsync(CancellationToken cancellationToken)
    {
        if (actor is not null) { return actor; }
        var user = http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) { throw new DomainError("authentication_required"); }
        var issuer = user.FindFirst("iss")!.Value;
        var subject = Guid.Parse(user.FindFirst("sub")!.Value);
        actor = await resolver.ResolveAsync(new VerifiedIdentity(issuer, subject), cancellationToken);
        return actor;
    }
}

public sealed record OptionalBearerContent;
