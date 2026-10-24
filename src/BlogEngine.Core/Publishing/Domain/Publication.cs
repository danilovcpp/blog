using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Publishing.Domain;

public enum PublicationState : short { Draft, Published }

public sealed record PublicationContent(string Title, string Slug, string Markdown, string? Description, string? CoverUrl,
    IReadOnlyList<Guid> CategoryIds, IReadOnlyList<Guid> TagIds)
{
    public PublicationContent Validate()
    {
        var title = TextRules.RequireTrimmed(Title, 200, "title");
        TextRules.RequireSlug(Slug);
        TextRules.RequireMarkdown(Markdown);
        if (Description is not null && TextRules.ScalarLength(Description, "description") > 500) { throw DomainError.Validation("description"); }
        if (CoverUrl is not null && (TextRules.ScalarLength(CoverUrl, "coverUrl") > 2048 ||
            !CoverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(CoverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))) { throw DomainError.Validation("coverUrl"); }
        PublicationClassification.Validate(CategoryIds, TagIds);
        return this with { Title = title, CategoryIds = CategoryIds.ToArray(), TagIds = TagIds.ToArray() };
    }
}

public sealed class Publication
{
    private Publication() { }
    public Guid Id { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Title { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string Markdown { get; private set; } = "";
    public string? Description { get; private set; }
    public string? CoverUrl { get; private set; }
    public PublicationState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? FirstPublishedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid Version { get; private set; }
    public long LikeCount { get; private set; }
    public long ViewCount { get; private set; }

    public static Publication Create(Guid authorId, PublicationContent content, DateTimeOffset now)
    {
        var publication = new Publication { Id = Guid.NewGuid(), AuthorId = authorId, CreatedAt = now };
        publication.Edit(content, now);
        return publication;
    }

    public void Edit(PublicationContent content, DateTimeOffset now)
    {
        RequireActive();
        content = content.Validate();
        if (FirstPublishedAt is not null && content.Slug != Slug) { throw new DomainError("invalid_state"); }
        if (State == PublicationState.Published && TextRules.TrimWhiteSpace(content.Markdown).Length == 0) { throw DomainError.Validation("markdown"); }
        Title = content.Title; Slug = content.Slug; Markdown = content.Markdown;
        Description = content.Description; CoverUrl = content.CoverUrl;
        Changed(now);
    }

    public void Publish(DateTimeOffset now)
    {
        RequireActive();
        if (State == PublicationState.Published) { return; }
        if (TextRules.TrimWhiteSpace(Markdown).Length == 0) { throw DomainError.Validation("markdown"); }
        State = PublicationState.Published; FirstPublishedAt ??= now; Changed(now);
    }

    public void Unpublish(DateTimeOffset now)
    {
        RequireActive();
        if (State == PublicationState.Draft) { return; }
        State = PublicationState.Draft; Changed(now);
    }

    public void Delete(DateTimeOffset now) { RequireActive(); DeletedAt = now; Changed(now); }
    private void RequireActive() { if (DeletedAt is not null) { throw new DomainError("resource_not_found"); } }
    private void Changed(DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(now)); }
        UpdatedAt = now; Version = Guid.NewGuid();
    }
}
