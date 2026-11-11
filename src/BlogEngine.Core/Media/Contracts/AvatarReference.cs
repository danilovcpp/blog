namespace BlogEngine.Core.Media.Contracts;

public sealed record AvatarReference(Guid FileId)
{
    public string SmallUrl => $"/api/v1/avatars/{FileId:D}/256.png";
    public string LargeUrl => $"/api/v1/avatars/{FileId:D}/512.png";
    public static AvatarReference? From(Guid? fileId) => fileId is { } id ? new(id) : null;
}
