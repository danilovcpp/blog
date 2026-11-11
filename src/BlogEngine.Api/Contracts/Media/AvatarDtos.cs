using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;

namespace BlogEngine.Api.Contracts.Media;

public sealed record AvatarStateDto(Guid? FileId, Guid Version, AvatarReference? Avatar)
{
    public static AvatarStateDto From(AvatarSnapshot state) => new(state.FileId, state.Version, state.Avatar);
}
