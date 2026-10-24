using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class PublicationStore(BlogDbContext db, IPublicationWriteScope scope) : IPublicationStore
{
    public async Task<Guid> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        content = content.Validate();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ValidateClassificationAsync(content, ct);
        var entity = Publication.Create(actor.Id, content, await PublicationWriteScope.UtcNowAsync(db, ct));
        db.Publications.Add(entity);
        AddClassification(entity.Id, content);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return entity.Id;
    }

    public async Task MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken ct)
    {
        await scope.ExecuteAsync(id, async (session, token) =>
        {
            var entity = ((PublicationWriteSession)session).Entity;
            AccessPolicy.DemandPublicationEdit(actor, entity.AuthorId, entity.State == PublicationState.Published, entity.DeletedAt is not null);
            if (readVersion() != entity.Version) { throw new DomainError("concurrency_conflict"); }
            switch (command)
            {
                case PublicationCommand.Edit:
                    var replacement = content!.Validate();
                    await ValidateClassificationAsync(replacement, token);
                    entity.Edit(replacement, session.UtcNow);
                    await RemoveClassificationAsync(id, token);
                    AddClassification(id, replacement);
                    break;
                case PublicationCommand.Publish: entity.Publish(session.UtcNow); break;
                case PublicationCommand.Unpublish: entity.Unpublish(session.UtcNow); break;
                case PublicationCommand.Delete:
                    entity.Delete(session.UtcNow);
                    await RemoveClassificationAsync(id, token);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(command));
            }
            return true;
        }, ct);
    }
    private async Task ValidateClassificationAsync(PublicationContent content, CancellationToken ct)
    {
        if (content.CategoryIds.Count != 0 && await db.Categories.CountAsync(x => content.CategoryIds.Contains(x.Id), ct) != content.CategoryIds.Count) { throw DomainError.Validation("categoryIds"); }
        if (content.TagIds.Count != 0 && await db.Tags.CountAsync(x => content.TagIds.Contains(x.Id), ct) != content.TagIds.Count) { throw DomainError.Validation("tagIds"); }
    }
    private void AddClassification(Guid id, PublicationContent content)
    {
        db.PublicationCategories.AddRange(content.CategoryIds.Select(x => new PublicationCategory(id, x)));
        db.PublicationTags.AddRange(content.TagIds.Select(x => new PublicationTag(id, x)));
    }
    private async Task RemoveClassificationAsync(Guid id, CancellationToken ct)
    {
        await db.PublicationCategories.Where(x => x.PublicationId == id).ExecuteDeleteAsync(ct);
        await db.PublicationTags.Where(x => x.PublicationId == id).ExecuteDeleteAsync(ct);
    }
}
