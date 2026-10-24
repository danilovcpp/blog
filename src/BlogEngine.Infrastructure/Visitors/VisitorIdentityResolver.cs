using System.Security.Cryptography;
using System.Text;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Contracts;

namespace BlogEngine.Infrastructure.Visitors;

public sealed record ResolvedVisitor(VisitorIdentity Identity, string? AnonymousVisitorId);

public sealed class VisitorIdentityResolver(AnonymousVisitorTokenService tokens)
{
    public ResolvedVisitor Resolve(CurrentActor? actor, string? anonymousToken)
    {
        if (actor is not null)
        {
            return new(new VisitorIdentity(VisitorKind.Authenticated,
                SHA256.HashData(Encoding.UTF8.GetBytes("BlogEngine:visitor:v1:authenticated:" + actor.Id.ToString("D")))), null);
        }
        if (!tokens.TryRead(anonymousToken, out var payload))
        {
            anonymousToken = tokens.Issue();
            if (!tokens.TryRead(anonymousToken, out payload)) { throw new InvalidOperationException("Visitor issuance failed."); }
        }
        var prefix = Encoding.UTF8.GetBytes("BlogEngine:visitor:v1:anonymous:");
        var input = new byte[prefix.Length + payload.Length]; prefix.CopyTo(input, 0); payload.CopyTo(input, prefix.Length);
        return new(new VisitorIdentity(VisitorKind.Anonymous, SHA256.HashData(input)), anonymousToken);
    }
}
