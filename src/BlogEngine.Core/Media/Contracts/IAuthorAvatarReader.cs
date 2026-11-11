namespace BlogEngine.Core.Media.Contracts;

// Reads on the caller's database snapshot; public reads never bootstrap avatar state.
public interface IAuthorAvatarReader
{
    Task<IReadOnlyDictionary<Guid, AvatarReference?>> ReadAsync(IReadOnlyCollection<Guid> authorIds, CancellationToken ct);
}
