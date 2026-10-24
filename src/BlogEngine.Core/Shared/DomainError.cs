namespace BlogEngine.Core.Shared;

public sealed class DomainError(string code, string? field = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;

    public static DomainError Validation(string field) => new("validation_failed", field);
}
