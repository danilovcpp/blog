using BlogEngine.Core.Media.Contracts;

namespace BlogEngine.Core.ReadModels;

public sealed record AuthorRef(Guid Id, AvatarReference? Avatar = null);
