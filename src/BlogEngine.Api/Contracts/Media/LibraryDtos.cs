using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Api.Contracts.Media;

public sealed record FileMetadataDto(Guid Id, Guid OwnerId, MediaPurpose Purpose, string Name, long Size, string ContentType,
    DateTimeOffset CreatedAt, string State, string? OriginalUrl, string? DisplayUrl)
{
    public static FileMetadataDto From(LibraryFile file) => new(file.Id, file.OwnerId, file.Purpose, file.Name, file.Size,
        file.ContentType, file.CreatedAt, "Ready", $"/api/v1/media/files/{file.Id:D}/content?variant=original", null);
}

public sealed record FilePageDto(int Page, int PageSize, IReadOnlyList<FileMetadataDto> Items, bool HasNextPage)
{
    public static FilePageDto From(LibraryPage page) => new(page.Page, page.PageSize, page.Items.Select(FileMetadataDto.From).ToArray(), page.HasNextPage);
}
