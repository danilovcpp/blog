using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlogEngine.Operator;

public static class CommandResultWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task WriteSuccessAsync(TextWriter writer, object result) => writer.WriteLineAsync(JsonSerializer.Serialize(result, Options));
    public static Task WriteErrorAsync(TextWriter writer, string code, Guid correlationId) => WriteSuccessAsync(writer, new
    {
        code,
        message = code switch
        {
            "invalid_arguments" => "Invalid command arguments.",
            "user_not_found" => "Local user was not found in this installation.",
            "invalid_configuration" => "Operator configuration is missing or invalid.",
            "cancelled" => "Command cancelled before commit; no changes were committed.",
            "outcome_unknown" => "Commit outcome is unknown. Check the audit using correlationId before retrying.",
            _ => "The database operation failed; no changes were committed.",
        },
        correlationId,
    });
}
