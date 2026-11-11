using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.ReadModels;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class PublicationStore(BlogDbContext db, IPublicationWriteScope scope, IAccountRoleWriteGuard roles,
    IPublicationMediaUsages media, WorkspaceQueries queries) : IPublicationStore
{
    public async Task<ClassifiedPublication> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        content = content.Validate();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await roles.LockAndReadAsync(actor.Id, ct);
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        await ValidateClassificationAsync(content, ct);
        var entity = Publication.Create(actor.Id, content with { CoverUrl = null }, await PublicationWriteScope.UtcNowAsync(db, ct));
        db.Publications.Add(entity);
        await db.SaveChangesAsync(ct);
        var cover = await media.ReplaceAsync(entity.Id, entity.AuthorId, content.CoverUrl, content.Media ?? new(), ct);
        entity.Edit(content with { CoverUrl = cover }, entity.UpdatedAt);
        AddClassification(entity.Id, content);
        await db.SaveChangesAsync(ct);
        var result = await queries.SnapshotAsync(entity.Id, actor.Id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ClassifiedPublication?> MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken ct)
    {
        return await scope.ExecuteAsync<ClassifiedPublication?>(id, async (session, token) =>
        {
            var entity = ((PublicationWriteSession)session).Entity;
            var currentActor = session.RoleActor!;
            AccessPolicy.DemandPublicationEdit(currentActor, entity.AuthorId, entity.State == PublicationState.Published, entity.DeletedAt is not null);
            if (readVersion() != entity.Version) { throw new DomainError("concurrency_conflict"); }
            switch (command)
            {
                case PublicationCommand.Edit:
                    var replacement = content!.Validate();
                    await ValidateClassificationAsync(replacement, token);
                    var cover = await media.ReplaceAsync(id, entity.AuthorId, replacement.CoverUrl, replacement.Media ?? new(), token);
                    entity.Edit(replacement with { CoverUrl = cover }, session.UtcNow);
                    await RemoveClassificationAsync(id, token);
                    AddClassification(id, replacement);
                    break;
                case PublicationCommand.Publish: entity.Publish(session.UtcNow); break;
                case PublicationCommand.Unpublish: entity.Unpublish(session.UtcNow); break;
                case PublicationCommand.Delete:
                    await media.RemoveAsync(id, entity.AuthorId, token);
                    entity.Delete(session.UtcNow);
                    await RemoveClassificationAsync(id, token);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(command));
            }
            await db.SaveChangesAsync(token);
            return command == PublicationCommand.Delete ? null : await queries.SnapshotAsync(id, actor.Id, token);
        }, ct, actor.Id);
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
