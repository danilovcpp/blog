using System.Globalization;
using System.Text;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public enum MediaPurpose : short { Attachment, PublicationImage, Avatar }
public enum MediaFileState : short { Pending, Ready, Deleted }

public static class MediaRules
{
    public const long DefaultAllowance = 1_073_741_824;
    public static long MaximumSize(MediaPurpose purpose) => purpose switch
    {
        MediaPurpose.Attachment => 50 * 1024 * 1024,
        MediaPurpose.PublicationImage => 10 * 1024 * 1024,
        MediaPurpose.Avatar => 5 * 1024 * 1024,
        _ => throw DomainError.Validation("purpose"),
    };

    public static string FileName(string value)
    {
        TextRules.ScalarLength(value, "originalName");
        if (value.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Control || r.Value is '/' or '\\'))
        { throw DomainError.Validation("originalName"); }
        var name = TextRules.TrimWhiteSpace(value);
        if (TextRules.ScalarLength(name, "originalName") is < 1 or > 255 || name is "." or ".." ||
            (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'))
        { throw DomainError.Validation("originalName"); }
        return name;
    }

    public static string ContentType(string value, MediaPurpose purpose)
    {
        var parts = value.Split('/');
        static bool Token(string part) => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
        if (parts.Length != 2 || !parts.All(Token)) { throw DomainError.Validation("declaredContentType"); }
        var canonical = value.ToLowerInvariant();
        if (purpose != MediaPurpose.Attachment && canonical is not ("image/jpeg" or "image/png" or "image/webp"))
        { throw DomainError.Validation("declaredContentType"); }
        return canonical;
    }

    internal static void Utc(DateTimeOffset now)
    { if (now.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(now)); } }
    internal static byte[] Hash(byte[] value)
    { if (value.Length != 32) { throw DomainError.Validation("sha256"); } return value.ToArray(); }
}

public sealed class MediaFile
{
    private MediaFile() { }
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public MediaPurpose Purpose { get; private set; }
    public string OriginalName { get; private set; } = "";
    public string DeclaredContentType { get; private set; } = "";
    public long OriginalSize { get; private set; }
    public string? VerifiedContentType { get; private set; }
    public MediaFileState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadyAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? OriginalObjectId { get; private set; }
    public Guid Version { get; private set; }

    public static MediaFile Create(Guid ownerId, MediaPurpose purpose, string name, string contentType, long size, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (ownerId == Guid.Empty || size <= 0 || size > MediaRules.MaximumSize(purpose)) { throw DomainError.Validation("declaredSize"); }
        return new()
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Purpose = purpose,
            OriginalName = MediaRules.FileName(name),
            DeclaredContentType = MediaRules.ContentType(contentType, purpose),
            OriginalSize = size,
            CreatedAt = now,
            Version = Guid.NewGuid()
        };
    }

    public void MakeReady(Guid? sourceId, string verifiedContentType, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (State != MediaFileState.Pending || (Purpose != MediaPurpose.Avatar && sourceId is null) ||
            (Purpose == MediaPurpose.Avatar && sourceId is not null)) { throw new DomainError("invalid_state"); }
        VerifiedContentType = MediaRules.ContentType(verifiedContentType, Purpose);
        OriginalObjectId = sourceId; State = MediaFileState.Ready; ReadyAt = now; Version = Guid.NewGuid();
    }

    public void Delete(DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (State == MediaFileState.Deleted) { return; }
        State = MediaFileState.Deleted; DeletedAt = now; OriginalObjectId = null; Version = Guid.NewGuid();
    }
}
