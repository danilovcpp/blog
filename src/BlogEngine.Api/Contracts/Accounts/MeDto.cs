namespace BlogEngine.Api.Contracts.Accounts;

public sealed record MeDto(Guid Id, IReadOnlyList<string> Roles);
