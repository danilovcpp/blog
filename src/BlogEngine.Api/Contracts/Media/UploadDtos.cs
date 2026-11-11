using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Api.Contracts.Media;

public sealed record UploadInitDto(string Purpose, string Name, long DeclaredSize, string DeclaredContentType);

public sealed record UploadResultDto(Guid UploadId, Guid FileId, MediaPurpose Purpose, DateTimeOffset CompletedAt, Guid? AppliedAvatarVersion)
{
    public static UploadResultDto From(UploadResult result) => new(result.UploadId, result.FileId, result.Purpose, result.CompletedAt, result.AppliedAvatarVersion);
}

public sealed record UploadDto(Guid Id, Guid FileId, MediaPurpose Purpose, string Name, long DeclaredSize, string DeclaredContentType,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, MediaUploadState State, UploadResultDto? Result, string? ErrorCode)
{
    public static UploadDto From(UploadSnapshot upload) => new(upload.Id, upload.FileId, upload.Purpose, upload.Name, upload.DeclaredSize,
        upload.DeclaredContentType, upload.CreatedAt, upload.ExpiresAt, upload.State, upload.Result is null ? null : UploadResultDto.From(upload.Result), upload.ErrorCode);
}
